#!/usr/bin/env python3
"""Send the Ambilight 'hello' frame and print the firmware banner.

Kept as one process with the port open for reading BEFORE the write, because the board only transmits
while the host has the port open with DTR asserted: a `printf > dev` followed by `head dev` closes the
port in between and the banner is lost.
"""
import os, select, struct, sys, termios, time, fcntl

dev = sys.argv[1] if len(sys.argv) > 1 else "/dev/ttyACM0"

# ver 1, type 3 (hello), seq 1, len 0, CRC-16/CCITT-FALSE(01 03 01 00 00) = 0xD3D6
HELLO = bytes([0xA5, 0x5A, 1, 3, 1, 0, 0, 0, 0xD6, 0xD3])

fd = os.open(dev, os.O_RDWR | os.O_NOCTTY)

# raw 8N1, no flow control - the same thing the TV app does in-process, for the same reason: IXON
# treats 0x11/0x13 in a pixel as flow control and stops the port for ever.
attrs = termios.tcgetattr(fd)
iflag, oflag, cflag, lflag, ispeed, ospeed, cc = attrs
iflag &= ~(termios.IGNBRK | termios.BRKINT | termios.PARMRK | termios.ISTRIP | termios.INLCR |
           termios.IGNCR | termios.ICRNL | termios.IXON | termios.IXOFF | termios.IXANY)
oflag &= ~termios.OPOST
lflag &= ~(termios.ECHO | termios.ECHONL | termios.ICANON | termios.ISIG | termios.IEXTEN)
cflag &= ~(termios.CSIZE | termios.PARENB | termios.CSTOPB | getattr(termios, "CRTSCTS", 0))
cflag |= termios.CS8 | termios.CREAD | termios.CLOCAL
cc[termios.VMIN] = 0
cc[termios.VTIME] = 0
termios.tcsetattr(fd, termios.TCSANOW, [iflag, oflag, cflag, lflag, ispeed, ospeed, cc])

# assert DTR+RTS: without DTR the CDC stack on the board considers the host absent and drops TX
fcntl.ioctl(fd, termios.TIOCMBIS, struct.pack("I", termios.TIOCM_DTR | termios.TIOCM_RTS))
time.sleep(0.3)

os.write(fd, HELLO)

buf = b""
deadline = time.time() + 3.0
while time.time() < deadline and b"\n" not in buf:
    r, _, _ = select.select([fd], [], [], 0.2)
    if r:
        chunk = os.read(fd, 256)
        if not chunk:
            break
        buf += chunk

os.close(fd)

text = buf.decode("ascii", "replace").strip()
if not text:
    print("NO BANNER - the firmware is not answering on this port")
    sys.exit(2)
print("banner:", text)
