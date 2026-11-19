#!/bin/sh
# Verify the TV <-> board link WITHOUT running the ambilight service.
#
# Run this ON THE TV (it needs /dev/ttyACM*). Stop the service first: the port can only be held by one
# process, and the service would eat the reply.
#
#     sh < /opt/ambilight/link-check.sh            # /dev/ttyACM1
#     DEV=/dev/ttyACM2 sh < /opt/ambilight/link-check.sh
#
# NOT `sh < file /dev/ttyACM1`: this TV's UEP policy then treats the device node as a script argument
# and refuses it ("the file is NOT signed!!"). Hence the environment variable.
#
# It answers three questions:
#   1. is the CDC node there, and is it the board (2e8a or 239a) and not the TV's own USB gadget
#      (04e8:2001, which also enumerates as a ttyACM)?
#   2. does the firmware read USB at all? The stock sketch does not: the app logs EAGAIN.
#   3. what does the firmware think it drives? The banner is "AMBI1 <count> <RGB|RGBW>" and the count
#      must equal ledCount in /opt/usr/share/ambilight/config.json.
#
# TWO THINGS ARE DELIBERATE:
#   * The port is kept open ONCE, for both directions (`exec 3<>`). Writing with `printf > /dev/ttyACM1`
#     and reading afterwards in a separate command opens and closes the port in between; the board's
#     CDC stack drops the banner when the host closes, so the write and the read must share one fd.
#     This - not a missing reply - is why the naive version of this check "hangs" or shows nothing.
#   * Raw mode is set before opening, for the same reason the app does it in-process: the default line
#     discipline reads 0x13 in a pixel as XOFF and stops output for ever, which looks like a dead board.

DEV=${DEV:-${1:-/dev/ttyACM1}}

if [ ! -e "$DEV" ]; then
    echo "FAIL: $DEV does not exist - the CDC module is probably not loaded: modprobe cdc_acm"
    exit 1
fi

# Resolve the VID/PID through sysfs so the answer does not depend on lsusb.
SYS="/sys/class/tty/$(basename "$DEV")/device"
VID=$(cat "$SYS"/../idVendor 2>/dev/null)
PID=$(cat "$SYS"/../idProduct 2>/dev/null)
echo "device: $DEV vid=$VID pid=$PID"
if [ "$VID" = "04e8" ]; then
    echo "FAIL: that is the TV's own USB gadget, not the board - use /dev/ttyACM1 (or whichever is 2e8a/239a)"
    exit 1
fi
case "$VID" in
    2e8a|239a) ;;    # Raspberry Pi (Pico SDK stack) / Adafruit (TinyUSB stack): the same board
    *) echo "WARN: expected vid=2e8a or 239a, got '$VID' - continuing anyway" ;;
esac

# min 0 time 30 = read returns after at most 3 s, so dd below cannot hang.
stty -F "$DEV" 115200 raw -echo -icanon -opost -ixon -ixoff min 0 time 30 2>/dev/null \
    || echo "WARN: could not set raw mode - the banner may be mangled"

exec 3<>"$DEV" || { echo "FAIL: cannot open $DEV - is the service still running?"; exit 1; }

# ver 1, type 3 (hello), seq 1, len 0, CRC-16/CCITT-FALSE of 01 03 01 00 00 = 0xD3D6
printf '\245\132\001\003\001\000\000\000\326\323' >&3
REPLY=$(dd bs=32 count=1 <&3 2>/dev/null | tr -d '\000\015')
exec 3<&-

if [ -z "$REPLY" ]; then
    echo "FAIL: no banner. The firmware is not reading USB: flash firmware/AmbilightScorpio."
    echo "      (A stock sketch, or a serial console that never reads, looks exactly like this.)"
    exit 2
fi

echo "banner: $REPLY"
case "$REPLY" in
    "AMBI1 "*) ;;
    *) echo "FAIL: reply is not an Ambilight banner"; exit 2 ;;
esac

FW_LEDS=$(echo "$REPLY" | cut -d' ' -f2)
FW_TYPE=$(echo "$REPLY" | cut -d' ' -f3)
CFG_LEDS=$(sed -n 's/.*"ledCount"[^0-9]*\([0-9][0-9]*\).*/\1/p' /opt/usr/share/ambilight/config.json 2>/dev/null)

echo "firmware: $FW_LEDS LEDs, $FW_TYPE"
echo "config  : ${CFG_LEDS:-?} LEDs"
if [ -n "$CFG_LEDS" ] && [ "$CFG_LEDS" != "$FW_LEDS" ]; then
    echo "FAIL: LED counts differ - oversized frames are dropped by the firmware, so nothing lights up."
    exit 3
fi

echo "OK: the link works in both directions."
