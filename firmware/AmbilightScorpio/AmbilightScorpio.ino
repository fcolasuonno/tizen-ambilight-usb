/*
 * Ambilight USB receiver for the Adafruit Feather RP2040 SCORPIO.
 *
 * The TV runs `AmbilightUsb` (a TizenAmbilight fork) as a root console service and writes one
 * pre-computed RGB frame per display frame to this board over USB CDC. This firmware owns the
 * physical strip: it re-times the pixels, applies a current limit, and blanks itself if the TV
 * stops sending - a dead app must not leave the wall lit on a stale colour.
 *
 * WHY THE FRAME IS PRE-COMPUTED ON THE TV: the TV already knows the panel content, the ring
 * geometry, the gamma/tone map and the letterbox crop. Duplicating any of that here would be a
 * second source of truth, and the RP2040 has no access to the video plane anyway.
 *
 * WIRE FORMAT (little endian, same as TizenAmbilight/src/SerialOutput.cs):
 *
 *     0xA5 0x5A | ver=1 | type | seq u16 | len u16 | data[len] | crc16 u16
 *
 *   ver   1
 *   type  1 = frame (data = R,G,B per LED)
 *         2 = blank (data = zeros; kept for symmetry, applied like a frame)
 *         3 = hello (asks for the one-line banner on the serial port)
 *   crc   CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection, no final xor)
 *         over ver .. data, i.e. every byte after the two magic bytes.
 *
 * A frame is applied only when its CRC matches. A truncated or corrupted frame is dropped and the
 * parser hunts for the next magic. `len` is capped at LED_COUNT*3 so a corrupted length can never
 * swallow seconds of stream (at 25 fps the whole stream is ~23 KB/s).
 *
 * CORE SPLIT: core 0 drains USB and parses; core 1 owns the strip and spends ~12 ms per
 * `show()` at 800 kHz for 300 RGBW pixels. Doing both on one core would stall the USB read for
 * 12 ms per frame, which is enough to overflow the CDC receive FIFO and tear the stream.
 */
#include <Arduino.h>
#include <Adafruit_NeoPixel.h>
#include <pico/mutex.h>

// ---------------------------------------------------------------- configuration
#define LED_PIN            16        // SCORPIO strand 0 (GPIO16..23 are the eight strands)
#define LED_COUNT          300       // must match ledCount in the TV's config.json

// Choose the strip. Only ONE strip is fitted, so only strand 0 is driven.
#define LED_TYPE_RGBW     (NEO_GRBW + NEO_KHZ800)   // SK6812 RGBW
#define LED_TYPE_RGB      (NEO_GRB  + NEO_KHZ800)   // WS2812B / SK6812 RGB
#define LED_TYPE          LED_TYPE_RGBW
#define HAS_WHITE         1        // must agree with LED_TYPE: 1 for RGBW, 0 for RGB

// 0 = leave the white LED dark (the TV's colour arrives as RGB; exact colour reproduction).
// 1 = extract min(r,g,b) into W and subtract it from RGB (brighter whites, lower RGB current).
#define USE_WHITE_CHANNEL  0

// Safety net for the 5 V supply. Each 255 channel is ~20 mA, so 300 pixels at full white would
// draw ~18 A; a 10 A supply cannot. Frames brighter than this are scaled down as a whole (one
// common factor) so hue is preserved. Raise it if the supply is upgraded.
#define MAX_MILLIAMPS      9000

// Blank the wall when the TV stops sending. The TV runs at 25 fps, so a 400 ms gap means the app
// is gone (or the cable is), not a dropped frame.
#define BLANK_AFTER_MS     400

// Reset the parser if a frame stops mid-way (corrupted length, unplugged cable, ...).
#define MIDFRAME_TIMEOUT_MS 120

#define MAGIC0 0xA5
#define MAGIC1 0x5A
#define VER    1
#define TYPE_FRAME 1
#define TYPE_BLANK 2
#define TYPE_HELLO 3

// ---------------------------------------------------------------- shared state
static uint8_t sPending[LED_COUNT * 3];   // core 0 parse target
static uint8_t sShared[LED_COUNT * 3];    // last complete frame
static uint8_t sLocal[LED_COUNT * 3];      // core 1 private copy
static mutex_t sMx;
static volatile bool     sNewFrame;
static volatile uint32_t sFrameNo;
static volatile uint32_t sDropped;

static Adafruit_NeoPixel strip(LED_COUNT, LED_PIN, LED_TYPE);

// ---------------------------------------------------------------- CRC
static inline uint16_t crcUpdate(uint16_t crc, uint8_t b)
{
    crc ^= (uint16_t)b << 8;
    for (int k = 0; k < 8; k++)
        crc = (crc & 0x8000) ? (uint16_t)((crc << 1) ^ 0x1021) : (uint16_t)(crc << 1);
    return crc;
}

// ---------------------------------------------------------------- receiver state machine
enum PState : uint8_t { P_MAGIC0, P_MAGIC1, P_HEAD, P_DATA, P_CRC_LO, P_CRC_HI };

static PState   pState = P_MAGIC0;
static uint8_t  hdr[6];
static uint8_t  hdrLen;
static uint16_t payLen, payPos;
static uint16_t crcCalc;
static uint8_t  crcLo;
static uint8_t  frameType;
static uint32_t lastByteMs;

static void resetParser()
{
    pState = P_MAGIC0;
    hdrLen = 0;
    payPos = 0;
}

static void commitFrame(uint8_t type, uint16_t len)
{
    uint16_t leds = len / 3;

    if (type == TYPE_HELLO) {
        // Deliberately the ONLY place this firmware ever transmits. The TV app never reads the
        // port, so an unsolicited banner would fill the TX FIFO and could block `show()`.
        char msg[48];
        int n = snprintf(msg, sizeof msg, "AMBI1 %u %s\n", (unsigned)LED_COUNT,
                         HAS_WHITE ? "RGBW" : "RGB");
        if (n > 0) Serial.write((const uint8_t *)msg, (size_t)n);
        return;
    }

    if (leds > LED_COUNT) { sDropped++; return; }

    mutex_enter_blocking(&sMx);
    if (leds) memcpy(sShared, sPending, leds * 3);
    if (leds < LED_COUNT) memset(sShared + leds * 3, 0, (LED_COUNT - leds) * 3);
    mutex_exit(&sMx);

    sFrameNo++;
    sNewFrame = true;
}

static inline void feed(uint8_t b)
{
    lastByteMs = millis();

    switch (pState) {
    case P_MAGIC0:
        if (b == MAGIC0) pState = P_MAGIC1;
        break;

    case P_MAGIC1:
        if (b == MAGIC1) { hdrLen = 0; crcCalc = 0xFFFF; pState = P_HEAD; }
        else if (b != MAGIC0) pState = P_MAGIC0;   // A5 A5 5A is still a valid resync
        break;

    case P_HEAD:
        hdr[hdrLen++] = b;
        crcCalc = crcUpdate(crcCalc, b);
        if (hdrLen < 6) break;
        frameType = hdr[1];
        payLen = (uint16_t)(hdr[4] | (hdr[5] << 8));
        if (hdr[0] != VER || payLen > LED_COUNT * 3 || frameType < TYPE_FRAME || frameType > TYPE_HELLO)
        {
            sDropped++;
            resetParser();
            break;
        }
        payPos = 0;
        pState = payLen ? P_DATA : P_CRC_LO;
        break;

    case P_DATA:
        sPending[payPos++] = b;
        crcCalc = crcUpdate(crcCalc, b);
        if (payPos >= payLen) pState = P_CRC_LO;
        break;

    case P_CRC_LO:
        crcLo = b;
        pState = P_CRC_HI;
        break;

    case P_CRC_HI:
        if (b == (uint8_t)(crcCalc >> 8) && crcLo == (uint8_t)(crcCalc & 0xFF))
            commitFrame(frameType, payLen);
        else
            sDropped++;                 // torn frame: drop it rather than show a wrong colour
        resetParser();
        break;
    }
}

// ---------------------------------------------------------------- core 1: the strip
static void fillStrip(const uint8_t *rgb, uint16_t leds)
{
    uint32_t units = 0;                 // sum of all channel values, 255 == ~20 mA
    for (uint16_t i = 0; i < leds * 3; i++) units += rgb[i];

    uint32_t scale = 256;               // 256 == unity (avoids float on the M0+)
    if (units) {
        // limit expressed in the same "channel units": mA * 255 / 20
        uint64_t limit = (uint64_t)MAX_MILLIAMPS * 255u / 20u;
        uint64_t draw  = (uint64_t)units;
        if (draw > limit) scale = (uint32_t)(limit * 256u / draw);
        if (scale == 0) scale = 1;
    }

    for (uint16_t i = 0; i < LED_COUNT; i++) {
        uint8_t r = 0, g = 0, b = 0;
        if (i < leds) {
            r = (uint8_t)((rgb[i * 3 + 0] * scale) >> 8);
            g = (uint8_t)((rgb[i * 3 + 1] * scale) >> 8);
            b = (uint8_t)((rgb[i * 3 + 2] * scale) >> 8);
        }
#if HAS_WHITE && USE_WHITE_CHANNEL
        uint8_t w = (r < g) ? (r < b ? r : b) : (g < b ? g : b);
        strip.setPixelColor(i, (uint8_t)(r - w), (uint8_t)(g - w), (uint8_t)(b - w), w);
#elif HAS_WHITE
        strip.setPixelColor(i, r, g, b, 0);
#else
        strip.setPixelColor(i, r, g, b);
#endif
    }
    strip.show();
}

void setup1()
{
    // NOTE: the mutex is initialised in setup() (core 0), not here. Core 0 starts parsing as soon as
    // the TV opens the port, which can be before core 1 has run setup1(); taking an uninitialised
    // mutex is undefined behaviour, and this ordering costs nothing.
    strip.begin();
    strip.clear();
    strip.show();
}

void loop1()
{
    static uint32_t lastFrameMs;
    static bool blanked = true;

    if (sNewFrame) {
        sNewFrame = false;
        mutex_enter_blocking(&sMx);
        memcpy(sLocal, sShared, sizeof sLocal);
        mutex_exit(&sMx);
        fillStrip(sLocal, LED_COUNT);
        lastFrameMs = millis();
        blanked = false;
    } else if (!blanked && (millis() - lastFrameMs) > BLANK_AFTER_MS) {
        blanked = true;
        strip.clear();
        strip.show();
    }
}

// ---------------------------------------------------------------- core 0: USB
void setup()
{
    mutex_init(&sMx);            // before core 0 can receive a frame (see setup1)
    Serial.begin(115200);
    sFrameNo = 0;
    resetParser();
}

void loop()
{
    while (true) {
        int c = Serial.read();
        if (c < 0) break;
        feed((uint8_t)c);
    }

    if (pState != P_MAGIC0 && (millis() - lastByteMs) > MIDFRAME_TIMEOUT_MS) {
        sDropped++;
        resetParser();
    }
}
