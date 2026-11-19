# SCORPIO firmware: `AmbilightScorpio`

Receiver for the Adafruit Feather RP2040 SCORPIO. It takes pre-computed RGB frames from the TV over
USB and drives **one** LED strip. Nothing here knows about video, geometry or tone mapping: the TV
sends final colours, this board only re-times them onto the strip.

## 1. What you need

| | |
|---|---|
| Board | Adafruit Feather RP2040 **SCORPIO** (8 level-shifted strands, GPIO16–23) |
| Strip | SK6812 RGBW **or** WS2812B, 300 LEDs (5 m at 60/m) |
| Supply | 5 V for the strip. 300 LEDs at full white ≈ 18 A; a 9 A limit is compiled in below |
| Flashing | the board over its own USB, from a PC with the Arduino IDE (or `arduino-cli`) |

The SCORPIO is powered and programmed over the same USB cable that later goes to the TV, so you can
develop at your desk and then move the cable.

## 2. Build and flash

Three ways. The sketch configures **300 LEDs, SK6812 RGBW, 9 A limit**; `LED_COUNT` and the strip type
are compiled in, so change them in the sketch and rebuild if your strip differs.

### A. Build a `.uf2` first, then drag-and-drop it

A `.uf2` is the compiled firmware image: hold **BOOTSEL** while plugging the board into a PC and it
appears as a USB drive named `RPI-RP2`; copying a `.uf2` onto that drive flashes the board, which then
reboots into the new sketch. No programmer or `picotool` is needed.

The repository does **not** ship a pre-built `.uf2` (`firmware/build/` is a local build directory and is
gitignored). Build it once with option B or C, then the file is at
`firmware/build/AmbilightScorpio.ino.uf2` and can be copied to any machine:

1. Hold **BOOTSEL** and plug the board's USB into the PC. It mounts as `RPI-RP2`.
2. Copy the `.uf2` onto that drive. The board reboots on its own.
3. Done — the drive disappears and the CDC port reappears.

### B. Arduino IDE

1. **Boards Manager** → install **Raspberry Pi Pico/RP2040** (Earle Philhower) — on the machine this was
   built on it is already installed as `rp2040:rp2040@6.4.0`.
2. Board: **Adafruit Feather RP2040 SCORPIO**. USB Stack: **TinyUSB** (the CDC port depends on it).
3. Open `AmbilightScorpio.ino`, **Upload**.

### C. arduino-cli

```sh
arduino-cli core install rp2040:rp2040 \
    --additional-urls https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
arduino-cli lib install "Adafruit NeoPixel"
arduino-cli compile --fqbn rp2040:rp2040:adafruit_feather_scorpio --output-dir build AmbilightScorpio
arduino-cli upload  --fqbn rp2040:rp2040:adafruit_feather_scorpio -p /dev/ttyACM0 AmbilightScorpio
```

Verified on this sketch with `arduino-cli 1.6.0`, `rp2040:rp2040@6.4.0`, `Adafruit NeoPixel@1.15.2`:
**57,388 bytes flash, 11,684 bytes RAM, no warnings from the sketch** (the only warnings come from
`rp2040_pio.h` inside the NeoPixel library and are harmless).

First flash without the strip connected is fine — nothing burns out, the board just drives a data line.


## 3. Configure the strip

Edit the block at the top of the sketch:

```c
#define LED_COUNT   300          // must equal ledCount in the TV's config.json
#define LED_TYPE    LED_TYPE_RGBW  // SK6812 RGBW ...
// #define LED_TYPE LED_TYPE_RGB   // ... or WS2812B (and set HAS_WHITE 0)
#define HAS_WHITE   1
#define USE_WHITE_CHANNEL 0      // 0 = white LED dark, colour arrives as RGB (recommended)
#define MAX_MILLIAMPS 9000       // safety net for a 10 A supply
```

`LED_COUNT` is the whole ring, i.e. what the TV's `ledCount` says. If the two disagree the firmware
drops the oversized frames and nothing lights up; the boot banner (below) prints the value it expects.

## 4. Wiring

```
 TV USB-A ── USB cable ── SCORPIO ── GPIO16 ──[330–470 Ω]── strip DIN
                              │                              │
                              GND ───────────────────────────┤ (strip GND)
 5 V PSU (+) ────────────────────────────────────────────────┤ (strip 5 V, both ends)
 5 V PSU (−) ──── SCORPIO GND ───────────────────────────────┘
```

- **Common ground is mandatory**: PSU −, SCORPIO GND and strip GND are one node. The data signal is
  referenced to it.
- Put the **10 A supply close to the strip**, feed both ends of the 5 m run, and add a **1000 µF**
  electrolytic across +/− at the strip head. Voltage drop is what makes the far end go pink, not the
  data line.
- The **330–470 Ω** resistor in series with DIN is cheap insurance for the first pixel.
- Do **not** power 300 LEDs from the TV's USB port. USB only carries data (and the board's own ~50 mA).
- The SCORPIO has a 5 V terminal block: use it for the strip supply so the board and the strip share
  5 V, and keep the USB cable for data. (USB VBUS and the terminal are diode-ORed on the board.)

## 5. Verify without the TV

### 5a. From a PC, before the board goes back to the TV

The board does not care where frames come from, so both directions can be tested at the bench:

```sh
python3 tools/hello.py /dev/ttyACM1          # -> banner: AMBI1 300 RGBW
python3 tools/test-pattern.py /dev/ttyACM1   # 6 s rainbow on the whole strip
python3 tools/test-pattern.py /dev/ttyACM1 --leds 30 --brightness 0.3 --seconds 15
```

`hello.py` opens the port for reading **before** it writes, because the board only transmits while a
host holds the port with DTR asserted — a `printf > /dev/ttyACM1` followed by `head /dev/ttyACM1` closes
the port in between and the banner is lost.

`test-pattern.py` sends the same wire format the TV does (`--mode rainbow|chase|solid|white`,
`--fps`, `--leds`, `--brightness`) and ends with a black frame. **Keep `--leds` and `--brightness` low
when the strip is powered from the board's USB:** 300 LEDs at half brightness is ~9 A and will collapse
the port. With the real 5 V supply connected, use `--leds 300 --brightness 0.5` for a full-strip test.

### 5b. From the TV

Both ways need the **service stopped**: only one process can hold the port, and the service would eat
the reply.

**From the TV** (this checks the whole link, including that the two LED counts agree):

```sh
sh < /opt/ambilight/link-check.sh        # note the '<': see below
```

It prints `OK: the link works in both directions.` or a specific failure: no device / wrong device /
no banner (firmware not reading USB) / LED counts differ. It must be run as `sh < file` rather than
`sh file`, because this TV's UEP policy refuses to execute unsigned script files ("the file is NOT
signed!!"). Piping the file in as stdin is not an execution and works.

**By hand**, if you want to see the bytes:

```sh
stty -F /dev/ttyACM1 115200 raw -echo -icanon -opost -ixon -ixoff min 0 time 10
printf '\xa5\x5a\x01\x03\x01\x00\x00\x00\xd6\xd3' > /dev/ttyACM1   # ver 1, type 3 (hello)
head -c 32 /dev/ttyACM1 | tr -d '\0'; echo
```

The reply is `AMBI1 300 RGBW` and tells you the firmware's `LED_COUNT` and strip type. (Anything that
is not read from the port blocks the firmware's transmit, which is why this is the *only* thing the
firmware ever sends.)



## 6. Wire format

Little endian, same as `TizenAmbilight/src/SerialOutput.cs`:

```
0xA5 0x5A | ver=1 | type | seq u16 | len u16 | data[len] | crc16 u16
```

| field | |
|---|---|
| `ver` | 1 |
| `type` | 1 = frame (data = R,G,B per LED), 2 = blank (data is zeros), 3 = hello |
| `crc` | CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection, no final xor) over `ver`..`data` |

A frame whose CRC does not match is dropped, never shown. `len` above `LED_COUNT*3` is rejected, so a
corrupted length cannot swallow seconds of stream. If no valid frame arrives for 400 ms the strip is
blanked — a killed TV app leaves a dark wall, not a frozen colour.

`python3 tools/decode-serial-dump.py <dump>` on the host decodes a dump written by the TV
(`serialDump` in `config.json`) with the same rules, which is the offline way to tell a firmware bug
from a colour-pipeline bug.

## 7. If nothing lights up

| symptom | check |
|---|---|
| no `/dev/ttyACM1` on the TV | `modprobe cdc_acm`; the module is not loaded at boot by default |
| log says "board is not draining (EAGAIN)" | the sketch is not reading USB — flash this firmware |
| LED 0 lights, the rest are dark | data reaches the first pixel but not past it: check the strip's direction (DIN vs DOUT) and the ground between segments |
| far end is pink/red at high brightness | supply droop: feed both ends, thicker wire, bigger PSU |
| colours wrong (green/red swapped) | `LED_TYPE`/`HAS_WHITE` do not match the strip |
| strip flashes then goes dark | the TV stopped sending: look for `output send` in `/opt/ambilight/run.log` |
