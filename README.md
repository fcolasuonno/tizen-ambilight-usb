# TizenAmbilight — USB / SCORPIO fork

This fork keeps upstream's capture, zone and colour pipeline and replaces the **output** and the
**hosting**: instead of sending WLED DRGB datagrams over Wi-Fi from a sandboxed TPK, it runs as a
root console process on a rooted Tizen TV and writes CRC-framed RGB frames over **USB serial** to an
**Adafruit Feather RP2040 SCORPIO**, which drives one LED strip.

Tested on a **QE77S90CATXXU (77", Tizen 9.0)** with a 299-LED SK6812 RGBW strip cut into five sections.
The capture libraries are undocumented Samsung ones; other models/firmwares are untested.

## How it works

```
video plane (960x540 NV12) -> perimeter zones -> HDR decode + BT.2020->709 gamut map
  -> EMA smoothing + adaptive tone curve -> final RGB per LED
  -> framed with magic + CRC16 -> /dev/serial/by-id/... (USB CDC)
  -> RP2040 SCORPIO (core 0 parses, core 1 drives the strip) -> one WS2812B/SK6812 strip
```

The board owns the physical ring→strand mapping; the TV sends one contiguous ring. The per-edge cut
counts are configured in `config.json`, so the preview, the zone sampling and the physical strip agree.

## What you need

- A **rooted** Samsung Tizen TV with the .NET 6 runtime (`/usr/bin/dotnet`) — e.g. Tizen 9.0. Root is
  required: a sandboxed app cannot open `/dev/ttyACM*` and needs a partner certificate for capture.
- **Vetro Shards** installed on the TV, with its root helper working (the SSH-server Shard is one).
- An **Adafruit Feather RP2040 SCORPIO** and one 5 V strip (SK6812 RGBW or WS2812B).
- A **5 V supply** sized for the strip (300 RGBW pixels at full white ≈ 18 A; the firmware caps at 9 A).
- A **PC** to flash the board once. There is no `picotool` on the TV.

## 1. Flash the SCORPIO

Build and flash [`firmware/AmbilightScorpio/AmbilightScorpio.ino`](firmware/AmbilightScorpio/AmbilightScorpio.ino)
with the Arduino IDE (board: *Adafruit Feather RP2040 SCORPIO*, TinyUSB) or `arduino-cli`, then copy the
resulting `.uf2` to the board's `RPI-RP2` drive while holding **BOOTSEL**.

The sketch defaults to **300 LEDs, SK6812 RGBW, `HAS_WHITE 1` with the white channel dark, 9 A limit**.
See [`firmware/README.md`](firmware/README.md) for wiring (common ground, 330–470 Ω in DIN, 1000 µF
across the supply, feed both ends) and a host-side test pattern.

## 2. Build and deploy the app

```sh
cd TizenAmbilight
docker run --rm -u 0 -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:6.0 \
  sh -c 'dotnet publish -c Release -o /src/out'
```

Copy `out/AmbilightUsb.dll`, `out/AmbilightUsb.deps.json` and `out/AmbilightUsb.runtimeconfig.json` to
`/opt/ambilight/` on the TV. The app is framework-dependent .NET 6 — no TPK, no signing, no Tizen Studio.

Write `/opt/usr/share/ambilight/config.json`:

```json
{
  "serialDevice": "/dev/serial/by-id/usb-Adafruit_Feather_RP2040_SCORPIO_<serial>-if00",
  "serialBaud": 115200,
  "serialDump": "",
  "ledCount": 300,
  "zoneCount": 300,
  "zoneTop": 97,
  "zoneRight": 53,
  "zoneBottom": 96,
  "zoneLeft": 53,
  "targetFps": 25,
  "outputEnabled": 1,
  "ppiZones": 24,
  "captureBackend": "plane",
  "gamma": 1.1,
  "saturation": 1.4,
  "black": 2,
  "spatial": 0.15,
  "peak": 400
}
```

Use the stable `/dev/serial/by-id/...` symlink for the board: the TV's own USB gadget and the board can
swap `/dev/ttyACM0`/`ACM1` between boots. The app also re-resolves the port by USB vendor id if it has
to. Leave `serialDump` empty in production — it is an unbounded debug dump.

Every setting is optional; [docs/configuration.md](docs/configuration.md) lists the rest. The four
`zone*` keys are the physical cut list; all zero restores upstream's proportional split.

## 3. Run and stop

```sh
# load the CDC driver (not persistent across a reboot)
modprobe cdc_acm

# start
cd /opt && DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
  /usr/bin/dotnet /opt/ambilight/AmbilightUsb.dll >> /opt/ambilight/run.log 2>&1 &

# stop — the ONLY safe way
touch /tmp/ambilight.stop
```

**Never `kill` the service.** The render loop is routinely inside a blocking native capture call;
tearing it down there wedges the TV's scaler until a power cycle. The stop file is polled at the top of
the loop; shutdown can take up to ~25 s, then it sends one blackout frame and exits cleanly.

A healthy log looks like:

```
status: 108 frames in 5.0s = 21.4 fps; avg capture=1.2ms zones=0.1ms total=1.4ms;
        send ok=412 fail=0 backend=video_plane; rate cap: asked 40.5ms got 40.7ms
```

`send ok` climbing with `fail=0` means the board is draining. `lastErr=EAGAIN (not draining)` means the
firmware is not reading USB (the stock sketch does not). The rate cap triples itself on a frozen
picture, so ~8 fps on a still image is by design, not a throughput limit.

## 4. Boot start and the settings panel

Install the Vetro Shard: copy **`dist/ambilight-1.0.0.vplugin`** to the TV and install it from
*Vetro Shards → Add a plugin from a file* (Developer mode must be on), or copy the contents of
[`vetro-plugin/ambilight/`](vetro-plugin/ambilight/) into
`/opt/usr/home/owner/share/vetro-plugins/ambilight/` as root. Then switch **Ambilight** on in the
dashboard — Shards never run until enabled.

On start the Shard loads `cdc_acm`, starts the service detached, and retries if Vetro's root helper is
not connected yet. Its tab has Start / Stop / Restart, a log tail, and a settings panel:

- **Geometry**: LED/zone counts, start offset, direction, band fraction, letterbox crop, and an
  **exact per-edge cut list** (top/right/bottom/left).
- **Layout preview**: a clickable 16:9 ring that draws the real five sections, the gap at bottom
  centre and the LED order. Click the ring to place LED 0.
- **Look & feel**: gamma, saturation, black level, neighbour blur, HDR peak, smoothing, target fps,
  still-image fade, punch — plus a *Vivid colours* preset.

See [`vetro-plugin/ambilight/README.md`](vetro-plugin/ambilight/README.md).

## Wire format

Little endian, shared by `TizenAmbilight/src/SerialOutput.cs` and the sketch:

```
0xA5 0x5A | ver=1 | type | seq u16 | len u16 | data[len] | crc16 u16
```

`type` 1 = frame (R,G,B per LED), 2 = blank, 3 = hello. CRC is CRC-16/CCITT-FALSE (poly `0x1021`,
init `0xFFFF`) over `ver`..`data`. A frame is 910 bytes for 300 LEDs; at 25 fps that is 23 kB/s.
Writes are non-blocking through a one-slot mailbox: if the board stalls, frames are dropped rather
than blocking capture. The firmware rejects bad CRCs, resyncs on the magic, blanks after 400 ms with
no frames, and answers the hello with `AMBI1 300 RGBW`.

`tools/decode-serial-dump.py` decodes a `serialDump` file off-TV; `tools/hello.py` and
`tools/test-pattern.py` drive the board from a PC; `tools/link-check.sh` verifies the link from the TV.

## Differences from upstream

| | upstream | this fork |
|---|---|---|
| Host | Tizen TPK service app | root console process outside the sandbox |
| Output | WLED DRGB over UDP/port 21324 | framed RGB over USB CDC to a SCORPIO |
| Persistence | TPK `on-boot` | Vetro Shard, `modprobe cdc_acm` + detached start |
| Stop | control token / app lifecycle | `/tmp/ambilight.stop`, never a signal |
| Geometry | proportional zone split | optional exact per-edge cut list with a bottom-centre gap |
| Settings | `config.json` / control channel | config file + Shard dashboard panel |

The capture ABI, zone engine, HDR/gamut handling and tone mapping are upstream's, unchanged.

## Docs

- [`docs/usb-serial.md`](docs/usb-serial.md) — architecture, build/deploy, config keys, wire format,
  measured numbers, troubleshooting.
- [`firmware/README.md`](firmware/README.md) — flashing, wiring, wire format, bench tests.
- [`vetro-plugin/ambilight/README.md`](vetro-plugin/ambilight/README.md) — Shard install and settings.
- [`docs/how-it-works.md`](docs/how-it-works.md), [`docs/configuration.md`](docs/configuration.md),
  [`docs/development.md`](docs/development.md) — upstream's capture-side documentation, still valid.

## Credits and license

Based on [RitwickVerma/TizenAmbilight](https://github.com/RitwickVerma/TizenAmbilight) (MIT). The
ambilight idea and DRGB protocol also owe a lot to
[Hyperion](https://github.com/hyperion-project/hyperion.ng),
[HyperHDR](https://github.com/awawa-dev/HyperHDR) and [WLED](https://github.com/wled/WLED); the Tizen
groundwork to [HyperTizen](https://github.com/reisxd/HyperTizen) and
[TizenGlow](https://github.com/DuckieOnQuacks/TizenGlow). Not affiliated with or endorsed by Samsung.

[MIT](LICENSE).
