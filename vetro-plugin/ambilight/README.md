# Ambilight Shard

Starts the USB ambilight service (`/opt/ambilight/AmbilightUsb.dll`) when the TV boots, and gives
the dashboard a Start / Stop / Restart button plus the tail of `run.log`.

## Why a Shard

`/etc` is read-only on this firmware, the capture libraries only work from a root process outside the
Tizen app sandbox, and the service cannot be a TPK. A Vetro Shard is the only mechanism on this TV
that already survives a reboot (`ctx.execute` runs as root, the same way the SSH server does).

## Install (by hand, over SSH)

```sh
P=/opt/usr/home/owner/share/vetro-plugins
mkdir -p $P/ambilight
# copy manifest.json, index.js and ui.html into $P/ambilight/
chown -R owner:users $P/ambilight
```

Then open **Vetro Shards** in the dashboard: the folder appears as *Ambilight* (source: copied in by
hand) and must be **switched on** — a Shard never runs until it is enabled. If the build refuses
unsigned Shards, turn on **Developer mode** first (Plugins → Add a plugin).

Switching it on runs `start()` immediately and at every boot afterwards.

## What it runs

`start`:

```sh
modprobe cdc_acm                        # not persistent across a reboot
setsid nohup /usr/bin/dotnet /opt/ambilight/AmbilightUsb.dll \
    >> /opt/ambilight/run.log 2>&1 < /dev/null &
```

`stop` touches `/tmp/ambilight.stop`. The service polls that file at the top of its render loop and
shuts down on its own thread — it is **never** SIGKILLed, because tearing it down while it is inside
`secvideo_api_capture_video_main` wedges the TV's scaler until the set is power-cycled.

## Files the service needs on the TV

| path | |
|---|---|
| `/opt/ambilight/AmbilightUsb.dll` | the app (+ `.deps.json`, `.runtimeconfig.json`) |
| `/opt/usr/share/ambilight/config.json` | settings, see the fork's README |

## Settings tab

The Shard's tab has a **Settings** section on top of Start / Stop / Restart and the log tail:

| group | fields |
|---|---|
| Geometry | LED count, zone count, **exact per-edge cut counts (top/right/bottom/left)**, start offset, reverse, edge band fraction, letterbox crop and threshold |
| Look & feel | smoothing (alpha), gamma, saturation, black level, neighbour blur, HDR peak nits, HDR/PQ input and BT.2020→BT.709 gamut mapping, target fps, still-image fade delay/level, scene punch |

- **Ambilight: ON/OFF** is a live output switch: it writes `enable=0/1` to `/tmp/amb-control` (the
  app's local control file when no `controlUrl` is set) and persists `outputEnabled` in config. The
  service keeps running; with output off no frames are sent and the SCORPIO blanks the strip after
  ~400 ms. Turning it back on resumes immediately.
- **Save** writes `/opt/usr/share/ambilight/config.json`; **Save & restart** also stops the service via
  the stop file and starts it again, because the app reads its config only at startup.
- A **layout preview** draws the 16:9 TV outline with all zone positions and the LEDs coloured in
  physical order (red→green→blue). It uses the same perimeter distribution and walk order as
  `ZoneEngine.cs`, so what you see is the order the strip actually receives. Click/drag the ring to
  place LED 0 (sets `mapOffset`); **Flip direction** toggles `mapReverse`; **Start top-left** and
  **My cut** are useful presets. On this TV the strip is one continuous 300-LED ring with no bottom
  gap: cut 97/53/97/53, starting at the bottom-right corner and running counter-clockwise
  (`mapOffset=149`, `mapReverse=1`).
- On load the panel probes the board with the hello frame and reads `AMBI1 <count> <type>` from its
  banner. LED count, zone count and the offset are clamped to that compiled-in value — an oversized
  config would silently drop every frame. When the service is running the port cannot be probed, so the
  last successful answer (kept in the Shard's own `config.json`) is used and labelled `cached`.
- Presets: **77″ / 300 LEDs** restores the values this TV is calibrated with (the same numbers in both
  `DEFAULTS` objects, including `hdr=0` / `gamut=0` for SDR); **Reset geometry** clears the
  offset/direction/crop settings without touching the look & feel values.
- Only the fields above are written; `serialDevice` (the stable `/dev/serial/by-id` symlink), the
  backend and all other keys are preserved untouched.

**Why file access goes through `ctx.execute`:** the Vetro process can read files inside its own plugin
folder but gets EACCES on `/opt/usr/share/...` and `/opt/ambilight/run.log` (Tizen/Smack). The Shard
reads and writes the app config and tails the log with root shell commands; only `ui.html` is read
with `fs`, because that lives in the Shard's own folder.

## Status fields

| field | meaning |
|---|---|
| `state` | `running` / `stopped` |
| `device` | whether `/dev/ttyACM1` exists (the SCORPIO) |
| `module` | whether `cdc_acm` is loaded |
| `serial` | last "serial port open" line (confirms the port was opened) |
| `capture` | last capture problem, if any (`rc=-2` = wedged scaler, needs a reboot) |
