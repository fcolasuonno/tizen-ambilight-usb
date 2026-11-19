#!/usr/bin/env python3
"""Drive the SCORPIO's strip from a PC, without the TV.

This is the "is the strip / PSU / wiring alive" test: the board does not care where frames come from,
so the same wire format the TV sends can be generated here. It also proves the firmware end to end
before the board is plugged back into the TV.

    python3 tools/test-pattern.py /dev/ttyACM0                  # 6 s rainbow sweep
    python3 tools/test-pattern.py /dev/ttyACM0 --mode solid --colour ff0000
    python3 tools/test-pattern.py /dev/ttyACM0 --mode chase --seconds 10
    python3 tools/test-pattern.py /dev/ttyACM0 --leds 60        # short strip on the bench

A frame is `0xA5 0x5A | ver=1 | type | seq u16 | len u16 | RGB... | crc16`; the CRC is
CRC-16/CCITT-FALSE over everything after the two magic bytes. Same rules as
`tools/decode-serial-dump.py`, which decodes the TV's dumps with the other end of the same code.
"""
import argparse, colorsys, os, struct, sys, termios, time, fcntl

TYPE_FRAME = 1
TYPE_BLANK = 2


def crc16(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return crc


def frame(ptype, seq, payload):
    body = struct.pack("<BBHH", 1, ptype, seq & 0xFFFF, len(payload)) + payload
    return b"\xA5\x5A" + body + struct.pack("<H", crc16(body))


def configure_raw(fd):
    import termios as t
    a = t.tcgetattr(fd)
    iflag, oflag, cflag, lflag, ispeed, ospeed, cc = a
    iflag &= ~(t.IGNBRK | t.BRKINT | t.PARMRK | t.ISTRIP | t.INLCR | t.IGNCR | t.ICRNL |
               t.IXON | t.IXOFF | t.IXANY)
    oflag &= ~t.OPOST
    lflag &= ~(t.ECHO | t.ECHONL | t.ICANON | t.ISIG | t.IEXTEN)
    cflag &= ~(t.CSIZE | t.PARENB | t.CSTOPB | getattr(t, "CRTSCTS", 0))
    cflag |= t.CS8 | t.CREAD | t.CLOCAL
    cc[t.VMIN] = 0
    cc[t.VTIME] = 0
    t.tcsetattr(fd, t.TCSANOW, [iflag, oflag, cflag, lflag, ispeed, ospeed, cc])
    # DTR must be asserted or the board's CDC stack drops everything it sends (and, on some builds,
    # ignores what arrives).
    fcntl.ioctl(fd, t.TIOCMBIS, struct.pack("I", t.TIOCM_DTR | t.TIOCM_RTS))


def hsv_bytes(h, s, v):
    r, g, b = colorsys.hsv_to_rgb(h % 1.0, s, v)
    return bytes((int(r * 255), int(g * 255), int(b * 255)))


def pattern(mode, leds, t, colour="ff0000", brightness=0.5):
    buf = bytearray(leds * 3)
    if mode == "rainbow":
        for i in range(leds):
            buf[i * 3:i * 3 + 3] = hsv_bytes(i / leds + t * 0.25, 1.0, brightness)
    elif mode == "chase":
        head = int(t * leds) % leds
        for i in range(leds):
            d = (i - head) % leds
            v = max(0.0, 1.0 - d / 30.0) * brightness
            buf[i * 3:i * 3 + 3] = hsv_bytes(i / leds, 1.0, v)
    elif mode == "white":
        buf[:] = b"\xFF" * (leds * 3)
    else:  # solid
        buf[:] = bytes.fromhex(colour) * leds
    if mode in ("solid", "white"):
        # one common factor, so hue/saturation are untouched - the same thing the firmware's own
        # current limiter does, but under our control here
        buf = bytearray(min(255, int(c * brightness)) for c in buf)
    return bytes(buf)


def main():
    global args
    p = argparse.ArgumentParser()
    p.add_argument("device")
    p.add_argument("--leds", type=int, default=300)
    p.add_argument("--seconds", type=float, default=6.0)
    p.add_argument("--fps", type=float, default=20.0)
    p.add_argument("--brightness", type=float, default=0.5,
                   help="0..1, default 0.5. Keep it low unless the strip has its own 5 V supply: "
                        "300 LEDs on USB power will collapse the port.")
    p.add_argument("--mode", choices=["rainbow", "chase", "solid", "white"], default="rainbow")
    p.add_argument("--colour", default="ff0000", help="hex RGB for --mode solid")
    args = p.parse_args()

    fd = os.open(args.device, os.O_RDWR | os.O_NOCTTY)
    configure_raw(fd)
    termios.tcflush(fd, termios.TCIFLUSH)
    time.sleep(0.2)

    seq = 0
    period = 1.0 / max(1.0, args.fps)
    t0 = time.time()
    sent = 0
    while True:
        now = time.time()
        t = now - t0
        if t >= args.seconds:
            break
        os.write(fd, frame(TYPE_FRAME, seq, pattern(args.mode, args.leds, t, args.colour, args.brightness)))
        seq = (seq + 1) & 0xFFFF
        sent += 1
        slack = period - (time.time() - now)
        if slack > 0:
            time.sleep(slack)

    # Explicit black frame, then let the firmware's own 400 ms idle blank confirm it.
    os.write(fd, frame(TYPE_BLANK, seq, bytes(args.leds * 3)))
    os.close(fd)
    print("sent %d frames (%d LEDs, %s, brightness %.2f) then a blank"
          % (sent, args.leds, args.mode, args.brightness))


if __name__ == "__main__":
    main()
