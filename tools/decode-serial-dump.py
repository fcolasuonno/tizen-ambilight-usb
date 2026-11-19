#!/usr/bin/env python3
"""Decode a serial dump written by AmbilightUsb (config key `serialDump`) and report whether the
stream is well formed.

The dump is the byte-exact output of TizenAmbilight/src/SerialOutput.cs, so this script is the
offline referee for the wire format the SCORPIO firmware has to parse:

    0xA5 0x5A | ver=1 | type | seq u16 | len u16 | data[len] | crc16 u16

It deliberately re-implements the firmware's rules (magic hunt, length cap, mid-frame timeout,
CRC check) so a dump that decodes here is a stream the firmware will also accept. `--show N`
prints the RGB of LED N per frame, which is how a colour pipeline change can be eyeballed without
a strip attached.

Usage:
    python3 tools/decode-serial-dump.py /opt/ambilight/frames.bin
    python3 tools/decode-serial-dump.py frames.bin --show 0 --show 150
"""
import argparse
import sys

MAGIC = b"\xa5\x5a"
VER = 1
TYPE_FRAME, TYPE_BLANK, TYPE_HELLO = 1, 2, 3
TYPE_NAMES = {TYPE_FRAME: "frame", TYPE_BLANK: "blank", TYPE_HELLO: "hello"}
MIDFRAME_TIMEOUT_MS = 120  # firmware resets a partial frame after this long without a byte


def crc16_ccitt(data):
    """CRC-16/CCITT-FALSE: poly 0x1021, init 0xFFFF, no reflection, no final xor."""
    crc = 0xFFFF
    for b in data:
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return crc


def encode(ftype, seq, data, version=VER):
    """Build one packet, mirroring SerialOutput.BuildFrame."""
    payload = bytes([version, ftype, seq & 0xFF, (seq >> 8) & 0xFF,
                     len(data) & 0xFF, (len(data) >> 8) & 0xFF]) + data
    return MAGIC + payload + crc16_ccitt(payload).to_bytes(2, "little")


def decode(blob, max_data=2048 * 3):
    """Parse like the firmware. Returns (frames, faults) where faults explains every rejection."""
    frames, faults = [], []
    i, n = 0, len(blob)
    while True:
        j = blob.find(MAGIC, i)
        if j < 0:
            break
        if j != i:
            faults.append(("resync", i, j - i, "skipped %d stray byte(s)" % (j - i)))
        if j + 8 > n:
            faults.append(("truncated-header", j, n - j, "stream ended inside a header"))
            break
        ver, ftype = blob[j + 2], blob[j + 3]
        seq = blob[j + 4] | (blob[j + 5] << 8)
        ln = blob[j + 6] | (blob[j + 7] << 8)
        if ver != VER:
            faults.append(("bad-version", j, 1, "ver=%d" % ver))
            i = j + 1
            continue
        if ftype not in TYPE_NAMES:
            faults.append(("bad-type", j, 1, "type=%d" % ftype))
            i = j + 1
            continue
        if ln > max_data:
            faults.append(("bad-length", j, 1, "len=%d > %d" % (ln, max_data)))
            i = j + 1
            continue
        end = j + 8 + ln + 2
        if end > n:
            faults.append(("truncated-body", j, n - j, "need %d more byte(s)" % (end - n)))
            break
        body = blob[j + 2:j + 8 + ln]
        got = blob[j + 8 + ln] | (blob[j + 9 + ln] << 8)
        want = crc16_ccitt(body)
        if got != want:
            faults.append(("bad-crc", j, 1, "got 0x%04x want 0x%04x" % (got, want)))
            i = j + 1
            continue
        data = blob[j + 8:j + 8 + ln]
        frames.append({"offset": j, "type": ftype, "seq": seq, "data": data,
                       "leds": ln // 3, "partial": ln % 3 != 0})
        i = end
    return frames, faults


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("dump", help="file written by serialDump")
    ap.add_argument("--show", type=int, action="append", default=[],
                    metavar="LED", help="print this LED's RGB per frame (repeatable)")
    ap.add_argument("--limit", type=int, default=0, help="decode at most this many frames")
    ap.add_argument("--quiet", action="store_true", help="only the summary and faults")
    args = ap.parse_args(argv)

    with open(args.dump, "rb") as f:
        blob = f.read()

    frames, faults = decode(blob)
    if args.limit:
        frames = frames[:args.limit]

    seqs = [f["seq"] for f in frames if f["type"] == TYPE_FRAME]
    gaps = sum(1 for a, b in zip(seqs, seqs[1:]) if (b - a) & 0xFFFF != 1)
    sizes = sorted({f["leds"] for f in frames})
    types = {}
    for f in frames:
        types[TYPE_NAMES[f["type"]]] = types.get(TYPE_NAMES[f["type"]], 0) + 1

    print("dump            : %s (%d bytes)" % (args.dump, len(blob)))
    print("packets         : %d  %s" % (len(frames), types))
    print("leds per frame  : %s" % (sizes,))
    print("sequence gaps   : %d" % gaps)
    print("faults          : %d" % len(faults))

    for kind, off, length, detail in faults[:20]:
        print("  %-16s @%d (%d B): %s" % (kind, off, length, detail))
    if len(faults) > 20:
        print("  ... %d more" % (len(faults) - 20))

    partial = [f for f in frames if f["partial"]]
    if partial:
        print("INVALID: %d packet(s) whose length is not a multiple of 3" % len(partial))

    if args.show and not args.quiet:
        for led in args.show:
            vals = []
            for f in frames:
                d = f["data"]
                if led * 3 + 2 < len(d):
                    vals.append("(%3d,%3d,%3d)" % (d[led * 3], d[led * 3 + 1], d[led * 3 + 2]))
            print("led %-4d %s" % (led, " ".join(vals[:40])))

    ok = not faults and not partial and len(frames) > 0
    print("RESULT: %s" % ("OK" if ok else "PROBLEMS"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
