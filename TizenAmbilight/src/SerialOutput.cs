using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TizenAmbilight
{
    /// <summary>
    /// Direct USB-serial output to an RP2040 "NeoPxl8" driver (Adafruit Feather RP2040 SCORPIO).
    ///
    /// Replaces WLED/UDP: there is no network in the LED path, no 490-LED DRGB limit, and the TV
    /// does the whole colour pipeline. The payload handed in is the same DRGB datagram
    /// <see cref="WledOutput.BuildDrgbMapped"/> already produces (2-byte WLED header + R,G,B per
    /// LED), so the tuned tone map, ring mapping and blackouts are unchanged; only the transport is.
    ///
    /// Wire format (little endian):
    ///     0xA5 0x5A | ver=1 | type | seq u16 | len u16 | data[len] | crc16-ccitt
    ///     type 1 = frame, 2 = blank
    /// CRC covers ver..data (every byte after the magic). The firmware drops a frame whose CRC is
    /// wrong instead of showing a torn picture.
    ///
    /// WHY THE RAW fd AND NOT FileStream:
    ///   * A tty is NOT a packet pipe. If the board stops draining (unplugged, hung, or flashed
    ///     with a sketch that does not read), a blocking write parks the writer thread in the
    ///     kernel - and a thread stuck there cannot be shut down cleanly. The port is therefore
    ///     opened O_NONBLOCK and a frame that cannot be written in full is DROPPED. A dropped
    ///     frame is invisible (25 fps) and the firmware resyncs on the next magic.
    ///   * The render loop must never block on I/O, so frames go through a ONE-SLOT mailbox: the
    ///     newest frame replaces an unsent one and a slow port cannot build a backlog. Note the
    ///     mailbox must be non-blocking in BOTH directions: BlockingCollection's parameterless
    ///     TryAdd actually BLOCKS when full, which is how the first shutdown attempt deadlocked.
    ///   * The tty line discipline eats bytes: IXON swallows 0x11/0x13 and OPOST rewrites 0x0A.
    ///     `stty raw` is therefore mandatory, not cosmetic, before any pixel data is written.
    /// </summary>
    public static class SerialOutput
    {
        /// <summary>Ceiling for the whole ring. The DRGB 490-LED limit is a WLED/UDP artefact and
        /// does not apply here; 2048 is far beyond any TV perimeter and keeps the frame under 8 KB.</summary>
        public const int MaxLeds = 2048;

        const byte Magic0 = 0xA5;
        const byte Magic1 = 0x5A;
        const byte Version = 1;
        const byte TypeFrame = 1;
        const byte TypeBlank = 2;

        // ---- libc (Linux/tizen, 32-bit ARM) ----
        const int O_WRONLY = 0x0001;
        const int O_NOCTTY = 0x0100;
        const int O_NONBLOCK = 0x0800;
        const int EINTR = 4;
        const int EAGAIN = 11;
        const int ENOENT = 2;
        const int EACCES = 13;
        const int EPIPE = 32;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        static extern int SysOpen([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        static extern int SysClose(int fd);

        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        static extern IntPtr SysWrite(int fd, [In] byte[] buf, UIntPtr count);

        [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
        static extern int TcGetAttr(int fd, [Out] byte[] termios);

        [DllImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
        static extern int TcSetAttr(int fd, int optionalActions, [In] byte[] termios);

        static readonly object _qLock = new object();
        static readonly AutoResetEvent _wake = new AutoResetEvent(false);
        static byte[] _pending;
        static int _fd = -1;

        static string _device = "";
        /// <summary>Path actually opened. Normally <see cref="_device"/>, but a boot-order race can put
        /// the LED board on a different ttyACM number - see <see cref="ResolvePath"/>.</summary>
        static string _path = "";
        static int _baud = 115200;
        static Thread _thread;
        static volatile bool _started;
        static ushort _seq;
        static string _lastError;
        /// <summary>How long writes have been failing with EAGAIN. Used to reopen a port that went
        /// stale when the board re-enumerated on another ttyACM - see WriterLoop.</summary>
        static readonly Stopwatch _eagain = new Stopwatch();

        /// <summary>Optional: append the exact frames handed to the port to this file (bounded), so
        /// the wire format can be decoded and checked off-device. Empty = off.</summary>
        public static string DumpPath { get; private set; }
        static System.IO.FileStream _dump;
        static long _dumpBytes;
        const long DumpLimit = 4L << 20;

        /// <summary>Last write failure, or null. Surfaced through the app's stats as the send error.</summary>
        public static string LastError { get { return _lastError; } }

        public static bool Configured { get { return !string.IsNullOrEmpty(_device); } }

        /// <summary>Select the port. Called once after config is read; a no-op if the device string
        /// is empty. Never opens the port here - the writer thread does, so a missing device cannot
        /// stall startup.</summary>
        public static void Configure(string device, int baud, string dumpPath)
        {
            _device = (device ?? "").Trim();
            _baud = baud > 0 ? baud : 115200;
            DumpPath = (dumpPath ?? "").Trim();
            if (_device.Length == 0) return;
            if (_started) return;
            _started = true;
            _thread = new Thread(WriterLoop);
            _thread.IsBackground = true;
            _thread.Name = "serial-out";
            _thread.Start();
            Ambilight.L("serial output: " + _device + " @ " + _baud +
                        " baud (USB CDC; baud is advisory)" +
                        (DumpPath.Length > 0 ? ", dumping frames to " + DumpPath : ""));
        }

        /// <summary>Queue one DRGB datagram. Returns null when queued (or when serial is not the
        /// selected transport), else the last I/O error. Never blocks.</summary>
        public static string Send(byte[] drgbPayload)
        {
            if (!Configured) return null;
            if (drgbPayload == null || drgbPayload.Length < 2) return "empty payload";

            int leds = (drgbPayload.Length - 2) / 3;
            byte[] frame = BuildFrame(TypeFrame, drgbPayload, 2, leds * 3);
            Dump(frame);
            Enqueue(frame);
            return _lastError;
        }

        /// <summary>All-zero frame: the deliberate "strip off" command.</summary>
        public static string SendBlackout(int ledCount)
        {
            if (!Configured) return null;
            if (ledCount < 1) ledCount = 1;
            if (ledCount > MaxLeds) ledCount = MaxLeds;
            byte[] frame = BuildFrame(TypeBlank, null, 0, ledCount * 3);
            Dump(frame);
            Enqueue(frame);
            return _lastError;
        }

        /// <summary>Final blackout on the shutdown path. Waits for the writer to put it on the wire
        /// (bounded), because the process is about to exit and a background thread would race the
        /// exit. Never throws, never blocks longer than <paramref name="budgetMs"/>.</summary>
        public static string SendBlackoutBlocking(int ledCount)
        {
            if (!Configured) return null;
            if (ledCount < 1) ledCount = 1;
            if (ledCount > MaxLeds) ledCount = MaxLeds;
            byte[] frame = BuildFrame(TypeBlank, null, 0, ledCount * 3);
            Dump(frame);
            if (_fd < 0 && !TryOpen()) return _lastError;
            if (!WriteAll(frame, 0, frame.Length, true, 500, out string err))
            {
                _lastError = err;
                return err;
            }
            _lastError = null;
            return null;
        }

        /// <summary>True when the writer has a usable open port (diagnostics only).</summary>
        public static bool PortOpen { get { return _fd >= 0; } }

        static void Enqueue(byte[] frame)
        {
            // One slot, non-blocking both ways: replace an unsent frame, never wait for space.
            lock (_qLock) { _pending = frame; }
            _wake.Set();
        }

        static byte[] Take()
        {
            _wake.WaitOne(250);
            lock (_qLock)
            {
                byte[] f = _pending;
                _pending = null;
                return f;
            }
        }

        static byte[] BuildFrame(byte type, byte[] src, int srcOff, int dataLen)
        {
            // magic(2) + ver(1) + type(1) + seq(2) + len(2) + data + crc(2)
            byte[] f = new byte[10 + dataLen];
            f[0] = Magic0;
            f[1] = Magic1;
            f[2] = Version;
            f[3] = type;
            ushort seq = unchecked(++_seq);
            f[4] = (byte)(seq & 0xFF);
            f[5] = (byte)(seq >> 8);
            f[6] = (byte)(dataLen & 0xFF);
            f[7] = (byte)(dataLen >> 8);

            if (src != null && dataLen > 0)
                Buffer.BlockCopy(src, srcOff, f, 8, dataLen);
            // else: bytes 8..8+dataLen-1 stay zero, which is exactly the blank frame

            ushort crc = Crc16(f, 2, 8 + dataLen - 2);
            f[8 + dataLen] = (byte)(crc & 0xFF);
            f[9 + dataLen] = (byte)(crc >> 8);
            return f;
        }

        static ushort Crc16(byte[] b, int off, int len)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < len; i++)
            {
                crc ^= (ushort)(b[off + i] << 8);
                for (int k = 0; k < 8; k++)
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        static void WriterLoop()
        {
            while (true)
            {
                byte[] frame = Take();
                if (frame == null) continue;

                if (_fd < 0 && !TryOpen()) continue;

                if (WriteAll(frame, 0, frame.Length, false, 0, out string err))
                {
                    if (_lastError != null) Ambilight.L("serial recovered: " + _path);
                    _lastError = null;
                    _eagain.Reset();
                }
                else
                {
                    // EAGAIN used to be ignored forever. That is wrong when the board is unplugged and
                    // re-enumerates on a DIFFERENT ttyACM: the TV's own gadget (04e8:2001) takes the old
                    // number, we keep writing into it, every frame fails, and the strip goes dark until
                    // somebody restarts the app (observed twice on this TV). A short EAGAIN burst is a
                    // normal full buffer; a sustained one means the port is wrong or the board is gone,
                    // so close it and let TryOpen() re-resolve the device by USB vendor id.
                    if (err != "EAGAIN")
                    {
                        _lastError = err;
                        Ambilight.L("serial write failed (" + _path + "): " + err + " - reopening");
                        ClosePort();
                        _eagain.Reset();
                        Thread.Sleep(400);
                    }
                    else
                    {
                        if (_lastError == null)
                            Ambilight.L("serial: board is not draining (EAGAIN) - dropping frames");
                        _lastError = "EAGAIN (not draining)";
                        if (!_eagain.IsRunning) _eagain.Start();
                        else if (_eagain.ElapsedMilliseconds >= 1500)
                        {
                            Ambilight.L("serial: no drain for " + _eagain.ElapsedMilliseconds +
                                        "ms - reopening and searching for the board again");
                            ClosePort();
                            _eagain.Reset();
                            Thread.Sleep(250);
                        }
                    }
                }
            }
        }

        /// <summary>Write a whole frame. On a non-blocking fd a partial write means the board's
        /// buffer is full: the remainder is dropped rather than retried in a tight loop, and the
        /// firmware's magic-search resyncs on the next frame.</summary>
        static bool WriteAll(byte[] buf, int off, int len, bool blocking, int budgetMs, out string err)
        {
            err = null;
            if (_fd < 0) { err = "no port"; return false; }

            var sw = Stopwatch.StartNew();
            int done = 0;
            while (done < len)
            {
                byte[] chunk = done == 0 ? buf : Slice(buf, off + done, len - done);
                IntPtr n = SysWrite(_fd, chunk, (UIntPtr)(uint)(len - done));
                long wrote = n.ToInt64();
                if (wrote > 0)
                {
                    done += (int)wrote;
                    continue;
                }

                int e = Marshal.GetLastWin32Error();
                if (e == EINTR) continue;
                if (e == EAGAIN)
                {
                    if (!blocking) { err = "EAGAIN"; return false; }
                    if (sw.ElapsedMilliseconds >= budgetMs) { err = "timeout (board not draining)"; return false; }
                    Thread.Sleep(5);
                    continue;
                }

                err = ErrName(e) + " (errno " + e + ")";
                return false;
            }
            return true;
        }

        static byte[] Slice(byte[] b, int off, int len)
        {
            byte[] r = new byte[len];
            Buffer.BlockCopy(b, off, r, 0, len);
            return r;
        }

        static string ErrName(int e)
        {
            switch (e)
            {
                case ENOENT: return "ENOENT (device gone)";
                case EACCES: return "EACCES (permission denied)";
                case EPIPE: return "EPIPE (board disconnected)";
                default: return "EIO";
            }
        }

        static bool TryOpen()
        {
            string path = _device;
            try
            {
                // Open FIRST, then put the port in raw mode on that same descriptor. Doing it the
                // other way round (a separate `stty` child) failed on this TV: stty did not return
                // within 2 s, so `raw` was never applied, and the line discipline then ate pixel
                // bytes - 0x13 in a colour was taken as XOFF and stopped output permanently, 0x0A
                // was rewritten, and the port wedged for good. termios is per-device state, so it
                // must be set on the descriptor we are about to write to.
                path = ResolvePath();
                int fd = SysOpen(path, O_WRONLY | O_NOCTTY | O_NONBLOCK);
                if (fd < 0)
                {
                    int e = Marshal.GetLastWin32Error();
                    string msg = ErrName(e) + " (errno " + e + ")";
                    if (msg != _lastError) Ambilight.L("serial open failed (" + path + "): " + msg);
                    _lastError = msg;
                    Thread.Sleep(1000);
                    return false;
                }
                _fd = fd;
                _path = path;
                bool raw = MakeRaw(fd);
                Ambilight.L("serial port open: " + path +
                            (path != _device ? " (auto-detected; config says " + _device + ")" : "") +
                            (raw ? " (raw 8N1, no flow control)" :
                             " WARNING: could not set raw mode - the tty may corrupt pixel data"));
                return true;
            }
            catch (Exception e)
            {
                string err = e.GetType().Name + ": " + e.Message;
                if (err != _lastError) Ambilight.L("serial open failed (" + path + "): " + err);
                _lastError = err;
                ClosePort();
                Thread.Sleep(1000);
                return false;
            }
        }

        /// <summary>USB vendor ids a RP2040 board can present: 2e8a is Raspberry Pi's (Pico SDK USB
        /// stack, which also calls the product "PicoArduino"), 239a is Adafruit's (TinyUSB stack, what
        /// the SCORPIO board definition selects). Both are the same board; checking only one meant the
        /// auto-detect silently did nothing after the first flash re-enumerated it.</summary>
        static bool IsBoardVid(string vid)
        {
            return vid == "2e8a" || vid == "239a";
        }

        /// <summary>Pick the LED board's ttyACM even if it did not get the configured number.
        ///
        /// The TV's own USB gadget (Samsung 04e8:2001) also enumerates as a ttyACM, and the boot
        /// order of the two is not fixed. Writing 910-byte pixel frames into the TV's gadget is
        /// silent: the app logs "send ok", the strip stays dark, and nothing points at the cause.
        /// So: if the configured node is not a RP2040 board (2e8a/239a) but one is present, use that
        /// one and say so in the log.
        ///
        /// A missing configured node is NOT second-guessed - the node can simply appear a second
        /// after modprobe, and the caller retries once a second.</summary>
        static string ResolvePath()
        {
            // A /dev/serial/by-id symlink names the DEVICE, not a slot number, and always resolves to
            // the correct tty even when the TV re-enumerates the two CDC ports in the opposite order.
            // Do not second-guess it: sysfs lookups below only understand ttyACM names.
            if (_device.StartsWith("/dev/serial/", StringComparison.Ordinal)) return _device;
            // Not there yet (modprobe just ran): wait for the configured node instead of guessing.
            if (!System.IO.File.Exists(_device)) return _device;
            string vid = UsbVid(_device);
            if (IsBoardVid(vid)) return _device;
            // The TV's own gadget has no USB device dir behind it (vid == null), so "null" is not a
            // reason to trust it: prefer a real board node whenever one exists.
            for (int i = 0; i < 8; i++)
            {
                string cand = "/dev/ttyACM" + i;
                if (cand == _device) continue;
                if (IsBoardVid(UsbVid(cand))) return cand;
            }
            return _device;
        }

        /// <summary>USB vendor id behind a tty node, or null when it cannot be read. The ".." is
        /// resolved by the kernel AFTER the "device" symlink (interface dir), which is what lands us
        /// in the USB device dir that holds idVendor.</summary>
        static string UsbVid(string dev)
        {
            try
            {
                string name = System.IO.Path.GetFileName(dev ?? "");
                if (string.IsNullOrEmpty(name)) return null;
                return System.IO.File.ReadAllText("/sys/class/tty/" + name + "/device/../idVendor").Trim();
            }
            catch { return null; }
        }

        /// <summary>Equivalent of <c>stty raw -echo</c>, done in-process on the open descriptor.
        ///
        /// Every byte that matters here is pixel data: without raw mode ISTRIP can clear the top
        /// bit, ICRNL turns 0x0D into 0x0A, OPOST rewrites 0x0A into 0x0D 0x0A, and IXON treats
        /// 0x11/0x13 as flow control - which stops the port until an XON that never comes. Offsets
        /// and constants are the Linux asm-generic ones used by 32-bit ARM.
        /// (tcflag_t iflag, oflag, cflag, lflag; cc_t c_line; cc_t c_cc[19]).</summary>
        static bool MakeRaw(int fd)
        {
            byte[] t = new byte[64];
            if (TcGetAttr(fd, t) != 0) return false;

            uint iflag = BitConverter.ToUInt32(t, 0);
            uint oflag = BitConverter.ToUInt32(t, 4);
            uint cflag = BitConverter.ToUInt32(t, 8);
            uint lflag = BitConverter.ToUInt32(t, 12);

            // IGNBRK|BRKINT|PARMRK|ISTRIP|INLCR|IGNCR|ICRNL|INPCK|IXON|IXANY|IXOFF
            iflag &= ~(0x0001u | 0x0002u | 0x0008u | 0x0020u | 0x0040u | 0x0080u | 0x0100u |
                       0x0010u | 0x0400u | 0x0800u | 0x1000u);
            oflag &= ~0x0001u;                       // OPOST
            // ISIG|ICANON|ECHO|ECHONL|IEXTEN
            lflag &= ~(0x0001u | 0x0002u | 0x0008u | 0x0040u | 0x8000u);
            cflag &= ~(0x0030u | 0x0040u | 0x0400u | 0x80000000u);   // CSIZE|CSTOPB|PARENB|CRTSCTS
            cflag |= 0x0030u | 0x0080u | 0x0800u;    // CS8|CREAD|CLOCAL

            Buffer.BlockCopy(BitConverter.GetBytes(iflag), 0, t, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(oflag), 0, t, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(cflag), 0, t, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(lflag), 0, t, 12, 4);
            t[16] = 0;                                // c_line = N_TTY
            t[17 + 5] = 0;                            // c_cc[VTIME]
            t[17 + 6] = 0;                            // c_cc[VMIN]

            return TcSetAttr(fd, 0 /* TCSANOW */, t) == 0;   // TCSANOW
        }

        static void ClosePort()
        {
            int fd = _fd;
            _fd = -1;
            if (fd >= 0) { try { SysClose(fd); } catch { } }
        }

        static void Dump(byte[] frame)
        {
            if (string.IsNullOrEmpty(DumpPath)) return;
            if (_dumpBytes >= DumpLimit) return;
            try
            {
                if (_dump == null)
                    _dump = new System.IO.FileStream(DumpPath, System.IO.FileMode.Create,
                                                     System.IO.FileAccess.Write,
                                                     System.IO.FileShare.ReadWrite);
                _dump.Write(frame, 0, frame.Length);
                _dump.Flush();
                _dumpBytes += frame.Length;
                if (_dumpBytes >= DumpLimit)
                {
                    Ambilight.L("serial dump complete: " + DumpPath + " (" + _dumpBytes + " bytes)");
                    try { _dump.Dispose(); } catch { }
                    _dump = null;
                }
            }
            catch (Exception e)
            {
                if (_lastError == null)
                    Ambilight.L("serial dump failed: " + e.GetType().Name + ": " + e.Message);
                DumpPath = "";
            }
        }
    }
}
