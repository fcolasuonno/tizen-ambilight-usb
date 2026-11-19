# USB-serial output: TV → SCORPIO → strip

This fork replaces the upstream Wi-Fi transport (WLED over UDP) with a direct **USB serial** link to an
Adafruit **Feather RP2040 SCORPIO**, and runs the app as a **root console service** instead of a TPK.
Everything above the transport (capture, zone layout, smoothing, tone mapping) is upstream code,
unchanged.

```
  Tizen TV                                                      SCORPIO            strip
  ┌──────────────────────────────────────────────────────────┐   ┌───────────┐   ┌──────────────┐
  │ secvideo_api_capture_video_main (960×540, in memory)      │   │ parser    │   │ WS2812B or   │
  │   → perimeter zones → EMA smoothing → tone map           │   │ CRC check │   │ SK6812 RGBW  │
  │   → 300 × RGB, ring order                                │──▶│ core0: USB│──▶│ 300 LEDs     │
  │   → SerialOutput: frame + CRC16                          │   │ core1: RMT│   │ (one strip)  │
  └──────────────────────────────────────────────────────────┘   └───────────┘   └──────────────┘
                                /dev/ttyACM1, CDC-ACM, raw 8N1
```

## Why this exists

The upstream app runs inside the Tizen app sandbox and therefore cannot open `/dev/ttyACM*`. It also
needs a partner certificate to load the capture libraries that way. Running the same code as a root
console process removes both problems: root can open the character device, and the capture libraries
load without the sandbox.

The SCORPIO is a better fit than WLED here: its 8 level-shifted outputs are meant for LED data, and the
mapping from "one ring of 300 LEDs" to "one physical strand" belongs on the board, not in the app.

## Files on the TV

| path | |
|---|---|
| `/opt/ambilight/AmbilightUsb.dll` (+ `.deps.json`, `.runtimeconfig.json`) | the app, framework-dependent .NET 6 |
| `/opt/usr/share/ambilight/config.json` | settings |
| `/opt/ambilight/run.log` | stdout/stderr of the service |
| `/opt/usr/home/owner/share/vetro-plugins/ambilight/` | the Vetro Shard that starts it at boot |

`/opt` is writable and survives a reboot on this firmware; `/etc` is read-only, which is why nothing is
installed as a systemd unit.

```json
{
  "serialDevice": "/dev/ttyACM1",
  "serialBaud": 115200,
  "serialDump": "",
  "ledCount": 300,
  "zoneCount": 300,
  "targetFps": 25,
  "outputEnabled": 1,
  "ppiZones": 24,
  "captureBackend": "plane"
}
```

| key | |
|---|---|
| `serialDevice` | the SCORPIO's CDC port. Not `ttyACM0` — on this TV that is a Samsung USB gadget (`04e8:2001`) |
| `serialBaud` | advisory for USB CDC; set it anyway so a real UART would behave |
| `serialDump` | if set, every frame is also appended to that file in the wire format. Use it with `tools/decode-serial-dump.py`; **leave it empty in production**, it is unbounded |
| `ledCount` / `zoneCount` | the whole ring. `zoneCount` = `ledCount` means one zone per LED |
| `zoneTop` / `zoneRight` / `zoneBottom` / `zoneLeft` | optional exact cut list. All four zero = the original proportional split from 960×540. On this TV: **97 / 53 / 96 / 53** (299 physical LEDs, gap at bottom centre). The leftover slot to reach `zoneCount` (300) is placed at the cut, after the left edge, so every real LED keeps its exact position. The bottom sampling also skips that one LED pitch so the unlit space is not sampled into a zone |
| `targetFps` | capture-rate cap (`0` = uncapped). The cap triples itself on a frozen picture |
| `gamma`, `saturation`, `black`, `spatial`, `peak` | colour shaping. Defaults 1.5 / 1 / 12 / 0.5 / 203 make bright colours pastel; the **Vivid colours** preset sets 1.1 / 1.4 / 2 / 0.15 / 400 |
| `wledIp` | leave empty; if set, the upstream UDP output is used instead of serial |

## Build

```sh
cd TizenAmbilight
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:6.0 \
    dotnet publish -c Release -o /src/out
```

Produces `out/AmbilightUsb.dll` and two small JSON files. The TV needs the .NET 6 runtime
(`/usr/bin/dotnet`, present in Tizen 9).

## Deploy and run

```sh
scp -P 2222 out/AmbilightUsb.* root@TV:/opt/ambilight/
scp -P 2222 config.json      root@TV:/opt/usr/share/ambilight/
ssh -p 2222 root@TV 'modprobe cdc_acm; \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 /usr/bin/dotnet /opt/ambilight/AmbilightUsb.dll'
```

`modprobe cdc_acm` is **not** persistent across a reboot on this firmware. The Shard does it on every
start.

## Stop: a file, never a signal

```sh
touch /tmp/ambilight.stop
```

The render loop checks that file at the top of every iteration and tears down **on its own thread**:
it parks, sends one blocking blackout frame, and exits 0. A `SIGKILL` (or a `SIGTERM` handler tearing
down from another thread) while the loop is inside the blocking native
`secvideo_api_capture_video_main` wedges the TV's scaler until the set is power-cycled — observed four
times while developing this fork; `rc=-2` on every later capture is the signature.

## Boot

`vetro-plugin/ambilight` is a Vetro Shard: on start it runs `modprobe cdc_acm` and starts the service
detached (`setsid nohup … &`), because `ctx.execute` has a 60 s cap and a shared queue. Enable it once in
**Vetro Shards** in the TV UI; Shards never run until they are switched on. Its tab also has a settings
panel (geometry and look & feel) that writes `config.json` and can restart the service immediately; see
[the Shard README](../vetro-plugin/ambilight/README.md).

## Wire format

Little endian:

```
0xA5 0x5A | ver=1 | type | seq u16 | len u16 | data[len] | crc16 u16
```

| field | |
|---|---|
| `type` | 1 = frame (`data` = R,G,B per LED, 900 bytes for 300 LEDs), 2 = blank, 3 = hello |
| `crc` | CRC-16/CCITT-FALSE (poly `0x1021`, init `0xFFFF`, no reflection, no final xor) over `ver`..`data` |

A frame is 910 bytes; at 25 fps that is 22.75 kB/s, far inside USB full-speed. Writes are non-blocking
and go through a one-slot mailbox drained by a background thread: if the board is not reading, frames
are **dropped**, never queued, so a stalled USB link can never slow down or block the capture loop.

The firmware and its wiring notes are in [`firmware/README.md`](../firmware/README.md).

## Testing the chain in pieces

| where | tool | answers |
|---|---|---|
| PC, board plugged in | `tools/hello.py` | is the firmware alive, and what does it think the strip is? |
| PC, board plugged in | `tools/test-pattern.py` | does the strip/PSU/wiring work, with no TV in the loop? |
| TV, service stopped | `sh < /opt/ambilight/link-check.sh` | device present, firmware reading USB, LED counts agree |
| TV, service running | `run.log` `status:` line | is the strip actually being fed, and at what rate |
| any machine | `tools/decode-serial-dump.py` | is the colour pipeline itself producing sane frames |

`test-pattern.py` uses the same framing code as the TV app, and the encoder is checked byte-for-byte
against `decode-serial-dump.py`, so a green strip from one and a wrong colour from the other points at
the capture half, not the link.


## Measured, on the QE77S90CATXXU

| | |
|---|---|
| first frame after start | ~300 ms |
| capture (960×540 plane) | 7–13 ms warm |
| zones + smoothing + tone map | < 1 ms |
| achieved rate, moving picture | 25 fps (the configured cap) |
| achieved rate, frozen picture | 8.3 fps (cap ×3 by design, saves TV power) |
| serial open → first frame on the wire | ~200 ms |

## Reading the log

One status line every 5 s:

```
status: 42 frames in 5.1s = 8.3 fps; avg capture=12.6ms zones=0.4ms total=13.2ms;
        send ok=1240 fail=0 backend=video_plane; rate cap: asked 68.8ms got 69.1ms
```

`send ok` climbing and `fail` static means the board is draining. `fail` climbing with
`lastErr=EAGAIN (not draining)` means the firmware is not reading USB — the stock sketch does not.
`lastErr=timeout (board not draining)` on a single blocking blackout at shutdown is harmless.
