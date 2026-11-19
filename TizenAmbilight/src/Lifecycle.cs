using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace TizenAmbilight
{
    static partial class Ambilight
    {
        // ================================================================ shutdown / blackout

        /// <summary>Set by OnShutdown (framework thread) and polled by the render loop. Without it the
        /// loop could send one more colour packet AFTER the shutdown blackout - last packet wins, so
        /// the strip would be left latched on a colour, which is the exact opposite of this path's
        /// purpose.</summary>
        static volatile bool _stopping;

        /// <summary>True when a datagram may be sent: output is enabled AND a strip is configured.</summary>
        static bool CanSend(Config cfg) { return cfg.OutputEnabled && (cfg.HasSerial || cfg.HasWled); }

        /// <summary>Exercise the shutdown path on demand. `OnTerminate` has NEVER been observed firing
        /// on this device - the app is always SIGKILLed by `tizen install` - so the blackout, the log
        /// flush and the buffer release were all written-but-unexercised. This runs the identical code
        /// path so it can be watched.</summary>
        public static void SelfTestShutdown()
        {
            L("SELFTEST: invoking the shutdown path deliberately");
            try { OnShutdown(); }
            catch (Exception e) { L("selftest shutdown threw: " + e.GetType().Name + ": " + e.Message); }
            try { FlushLog(); } catch { }
            // SHIP the shutdown evidence. FlushLog writes to the TV's own disk, which is unreachable -
            // `sdb shell` is blocked on this firmware - and the app exits BEFORE the next 5 s stats post,
            // so the shutdown lines were being written and never seen. A bounded synchronous post (3 s
            // timeout) makes them visible without risking a hang on a dead collector.
            try
            {
                List<string> tail;
                lock (_logLock) { int take = Log.Count < 12 ? Log.Count : 12;
                                  tail = Log.GetRange(Log.Count - take, take); }
                var rep = new Dictionary<string, object>
                {
                    { "app", AppId }, { "version", Version }, { "phase", "shutdown" },
                    { "ts_utc", DateTime.UtcNow.ToString("o") },
                    { "reason", "SelfTestShutdown / normal terminate" },
                    { "uptime_s", (DateTime.UtcNow - ProcessStartUtc).TotalSeconds },
                    { "log", new List<object>(tail) },
                };
                if (_cfg != null && _cfg.HasTelemetry)
                {
                    string err = Json.Post(_cfg.Endpoint + "/probe", Json.ToJson(rep), 3);
                    Console.WriteLine("[amb] shutdown report -> " + (err ?? "OK"));
                }
            }
            catch { }
            Thread.Sleep(300);                      // let the blackout datagram leave
            L("SELFTEST: shutdown path complete, exiting");
            Environment.Exit(0);
        }

        /// <summary>Persist the log tail. The fatal handler calls this so the last lines survive a
        /// crash - they are usually the only evidence of what went wrong.</summary>
        public static void FlushLog()
        {
            try
            {
                string dir = "/home/owner/ambilight";
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                List<string> snap;
                lock (_logLock) { snap = new List<string>(Log); }
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "last.log"), snap.ToArray());
            }
            catch { }
        }

        /// <summary>Set by the signal handler; the render loop exits at the top of its next
        /// iteration. Purely a flag so the caller can decide when it is safe to tear down the
        /// native capture.</summary>
        static readonly ManualResetEventSlim _loopParked = new ManualResetEventSlim(false);

        /// <summary>The supported way to stop the service. A FILE, not a signal, because the
        /// render loop is regularly inside a blocking native capture call (D state, up to ~7 s
        /// observed on a wedged scaler). In that state the runtime's SIGTERM handling did not reach
        /// the managed handler at all: the process died, the handler never logged, and because the
        /// capture was in flight the TV's scaler was left wedged. Only the thread that owns the
        /// capture may tear it down, and only once it is out of that call - so the request is
        /// polled at the top of the loop and the shutdown runs ON THAT THREAD.</summary>
        const string StopFilePath = "/tmp/ambilight.stop";

        /// <summary>True when a stop was requested (signal flag or stop file). Called once per
        /// loop iteration: one stat() per frame is cheaper than the frame itself.</summary>
        public static bool StopRequested()
        {
            if (_stopping) return true;
            try
            {
                if (System.IO.File.Exists(StopFilePath))
                {
                    _stopping = true;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Clear any stale stop request: a stop file left behind by a previous run must
        /// not make this one exit immediately.</summary>
        public static void ClearStopRequest()
        {
            try { if (System.IO.File.Exists(StopFilePath)) System.IO.File.Delete(StopFilePath); } catch { }
            _stopping = false;
        }

        /// <summary>Ask the render loop to stop making capture calls. The loop signals
        /// <see cref="WaitLoopParked"/> before it returns.</summary>
        public static void RequestStop()
        {
            _stopping = true;
        }

        /// <summary>Wait until the render loop has left its capture section.
        ///
        /// THIS IS THE ONE THAT MATTERS. Abandoning an in-flight
        /// secvideo_api_capture_video_main - by exiting the process, or by freeing the param
        /// block underneath it - wedges the TV's scaler: every later capture returns rc=-2 until
        /// the set is power-cycled. That was measured 3/3 on the reference TV by the upstream
        /// author, and again today: a SIGTERM'd run that never parked left the scaler dead.
        /// A capture call can take up to ~1 s when the scaler is busy, so the budget is generous.
        /// </summary>
        public static bool WaitLoopParked(int budgetMs)
        {
            // Called from the render loop itself (the `shutdown` control token): there is no
            // capture in flight at that point, and waiting would just deadlock for the budget.
            if (Thread.CurrentThread.ManagedThreadId == _loopThreadId) return true;
            try { return _loopParked.Wait(budgetMs); } catch { return false; }
        }

        static int _loopThreadId = -1;

        /// <summary>Start of the render loop's first iteration: remember who owns the native
        /// capture so a caller on that same thread does not wait for it to park.</summary>
        static void MarkLoopThread()
        {
            _loopThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>The render loop has left its capture section and is about to return.</summary>
        static void MarkLoopParked()
        {
            _loopParked.Set();
        }

        /// <summary>Called from the framework terminate callback. Blanks the strip so it is never
        /// left showing a stale frame. Does nothing unless output can be sent. Never throws.</summary>
        public static void OnShutdown()
        {
            _stopping = true;
            bool parked = WaitLoopParked(4000);
            L("shutdown: loop parked=" + parked);
            if (!parked)
                L("shutdown: render loop did NOT park within 4 s - capture may be in flight");
            try { PpiCapture.Shutdown(); } catch { }   // sampler thread: no native ppi call after this
            Config c = _cfg;
            if (c == null || !CanSend(c)) return;
            try
            {
                // Blocking (bounded): the process exits immediately afterwards, so a background
                // writer thread would race the exit and the strip could stay latched on the last
                // frame. This waits for the bytes to reach the board, then returns.
                string err = c.HasSerial
                    ? SerialOutput.SendBlackoutBlocking(c.LedCount)
                    : WledOutput.SendBlackout(c.WledIp, c.WledPort, c.LedCount);
                L("shutdown blackout (" + c.LedCount + " LEDs via " +
                  (c.HasSerial ? c.SerialDevice : "wled") + ") -> " + (err == null ? "OK" : err));
                if (parked)
                    VideoPlaneCapture.Shutdown();  // free the plane buffers only once nobody uses them
            }
            catch (Exception e)
            {
                L("shutdown blackout threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>A run of consecutive frame-less iterations means the strip would sit latched on
        /// the last good frame, so blank it. Fires ONCE per streak — at exactly
        /// <see cref="BlankAfterNoFrameMs"/> of no usable frame —
        /// and only when output is enabled. Never throws.</summary>
        static DateTime _lastGoodFrameUtc = DateTime.MinValue;
        static bool _blankedForStall;
        /// <summary>Last time a stall blackout was sent, so it can be re-sent before WLED's live-mode
        /// timeout expires and it reverts to its own default colour.</summary>
        static DateTime _lastBlackoutUtc = DateTime.MinValue;
        /// <summary>Must stay WELL UNDER the blackout packet's timeout byte (WledOutput
        /// .BlackoutTimeoutSeconds) or WLED un-latches between sends and flashes its own colour.</summary>
        const int BlackoutHoldMs = 1000;

        /// <summary>Mark that a usable frame just arrived. Called from BOTH capture backends: it
        /// previously ran only on the video-plane path, so with captureBackend=file MaybeBlackout()
        /// short-circuited on DateTime.MinValue forever and the strip was NEVER blanked after a stall.</summary>
        static void NoteGoodFrame()
        {
            _lastGoodFrameUtc = DateTime.UtcNow;
            _blankedForStall = false;
            _lastBlackoutUtc = DateTime.MinValue;   // frames are back: no need to hold
        }

        /// <summary>Cheap content signature over whatever buffer the backend produced, so the stale
        /// fade works on both backends (it too was video-plane-only, leaving a frozen picture holding
        /// the strip lit forever on the file path).</summary>
        static void NoteContent(byte[] buf, Stats st)
        {
            if (buf == null || buf.Length == 0) return;
            int sig = 17;
            for (int i = 0; i < buf.Length; i += 157) sig = unchecked(sig * 31 + buf[i]);
            if (sig == st.ContentSig) { st.ContentUnchanged++; }
            else
            {
                st.ContentSig = sig;
                st.ContentUnchanged = 0;
                st.LastContentChangeUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Target gamma for a given scene brightness.
        ///
        /// Anchored on the surround-gamma rule that a darker viewing condition calls for a higher gamma
        /// - the Philips patent states "a dark surround requires a gamma of about 1.5", and the
        /// (secondhand) De Marsh figures run 1.0 bright / 1.2 dim / 1.5 dark. Our "surround" is the
        /// scene itself, so a dark scene takes the top of the range and a bright one the bottom.
        ///
        /// Interpolation is linear in PQ code, which is roughly logarithmic in nits (PQ is itself a
        /// log-ish encoding), so the adaptation is perceptually even rather than linear in luminance.
        /// 16 is the black level and 120 is a comfortably bright scene on this panel.</summary>
        static double SceneGammaTarget(int medianCode, Config cfg)
        {
            double t = (medianCode - 16.0) / 104.0;
            if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
            double g = cfg.GammaMax - (cfg.GammaMax - cfg.GammaMin) * t;
            if (g < cfg.GammaMin) g = cfg.GammaMin;
            if (g > cfg.GammaMax) g = cfg.GammaMax;
            return g;
        }

        /// <summary>Send a stall/standby blackout, rate-limited to BlackoutHoldMs.
        ///
        /// WLED does not stay on our black by itself: the datagram carries a timeout byte, after which
        /// WLED leaves live mode and reverts to its OWN segment colour (default WLED ORANGE). Anything
        /// that blanks ONCE and then stops therefore turns orange a few seconds later. This is the one
        /// place that knows how to keep the strip latched to black, and BOTH the stall path and the
        /// standby path must go through it for as long as the blank state lasts.</summary>
        static void HoldBlackout(Config cfg, string why)
        {
            if (!CanSend(cfg)) return;
            if (_lastBlackoutUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _lastBlackoutUtc).TotalMilliseconds < BlackoutHoldMs) return;

            bool firstBlank = !_blankedForStall;
            _lastBlackoutUtc = DateTime.UtcNow;
            _blankedForStall = true;
            string err = cfg.HasSerial
                ? SerialOutput.SendBlackout(cfg.LedCount)
                : WledOutput.SendBlackout(cfg.WledIp, cfg.WledPort, cfg.LedCount);
            if (firstBlank)
            {
                if (err == null) L(why);
                else L("blackout send failed: " + err);
            }
        }

        /// <summary>Blank once the picture has been gone for BlankAfterNoFrameMs. Idempotent: it fires
        /// exactly once per stall and re-arms automatically when frames return.</summary>
        static void MaybeBlackout(Config cfg, Stats st)
        {
            if (!CanSend(cfg)) return;
            if (_lastGoodFrameUtc == DateTime.MinValue) return;
            double ageMs = (DateTime.UtcNow - _lastGoodFrameUtc).TotalMilliseconds;
            if (ageMs < BlankAfterNoFrameMs) return;

            // HOLD THE BLACK, do not fire once and stop.
            //
            // The blackout datagram carries a TIMEOUT byte: WLED un-latches that many seconds after the
            // last packet we send, leaves live mode, and reverts to its OWN segment colour - which is
            // the default WLED ORANGE (255,160,0). Sending a single blackout therefore produced a strip
            // that went black for about a second and then glowed orange for the entire stall. Observed
            // live: 46 s of rc=-1 (playback stopped, back to a menu) with the whole ring orange, and
            // WLED reporting live=False with no source IP - i.e. it was not us driving it at all.
            //
            // Re-sending inside the timeout keeps WLED latched to OUR black for as long as the stall
            // lasts. Cheap: one small UDP packet per second while stalled, and none at all while frames
            // are flowing.
            if (_lastBlackoutUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _lastBlackoutUtc).TotalMilliseconds < BlackoutHoldMs) return;

            HoldBlackout(cfg, "strip blanked: no usable frame for " + (long)ageMs + " ms");
        }
    }
}
