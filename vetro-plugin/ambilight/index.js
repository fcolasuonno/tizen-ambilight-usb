'use strict';
// Ambilight Shard: starts the USB ambilight service when the TV boots and gives the dashboard a
// start/stop/log view of it.
//
// WHY A PLUGIN AT ALL: /etc is read-only on this firmware and the capture libraries only work from
// a root process outside the Tizen app sandbox, so the service cannot be a TPK and cannot be a
// systemd unit. A Vetro Shard is the mechanism that already survives a reboot on this TV (the SSH
// server is one), and `ctx.execute` runs as root.
//
// Two things are deliberate:
//   * The service is started DETACHED (`setsid nohup ... &` with all three descriptors redirected).
//     ctx.execute has a 60 s cap and a shared command queue, so a foreground child would either be
//     killed or block every other Shard.
//   * Stop uses a stop FILE, never SIGKILL. The render loop is routinely inside a blocking native
//     capture call; tearing the process down there wedges the TV's scaler until the set is power
//     cycled (measured, 4 times). The app polls /tmp/ambilight.stop and shuts itself down on the
//     only thread allowed to leave that call.
var fs = require('fs');
var path = require('path');

var APP = '/opt/ambilight/AmbilightUsb.dll';
var LOG = '/opt/ambilight/run.log';
var STOPFILE = '/tmp/ambilight.stop';
// The check MUST be anchored to the start of the command line. An unanchored literal matched the
// `sh -c` wrapper that is itself running this script (its arguments contain the same string), so
// start() always reported "already running" and launched nothing; a bracket-escaped regex matched
// nothing at all on this TV's busybox. The anchored form matches only the real dotnet process.
var MATCH = '^/usr/bin/dotnet /opt/ambilight/AmbilightUsb.dll';
var CONFIG = '/opt/usr/share/ambilight/config.json';

// Fields the settings tab may change, with the same clamps the C# config reader applies. Geometry is
// the part that decides WHERE light goes; the rest decides HOW it looks. Everything else in config.json
// (serialDevice, backend, ...) is preserved untouched on save.
var SPECS = {
    outputEnabled: ['int', 0, 1],
    ledCount:   ['int', 1, 2048],
    zoneCount:  ['int', 1, 2048],
    zoneTop:    ['int', 0, 2048],
    zoneRight:  ['int', 0, 2048],
    zoneBottom: ['int', 0, 2048],
    zoneLeft:   ['int', 0, 2048],
    mapOffset:  ['int', 0, 2047],
    mapReverse: ['int', 0, 1],
    bandFrac:   ['num', 0.01, 0.50],
    bars:       ['int', 0, 1],
    barRows:    ['int', 0, 1],
    barThresh:  ['int', 0, 255],
    alpha:      ['num', 0, 1],
    gamma:      ['num', 0.5, 4],
    adaptiveGamma: ['num', 0, 1],
    saturation: ['num', 0.5, 1],
    black:      ['num', 0, 200],
    spatial:    ['num', 0, 1],
    peak:       ['num', 100, 4000],
    hdr:        ['int', 0, 1],
    gamut:      ['int', 0, 1],
    targetFps:  ['int', 0, 120],
    staleMs:    ['num', 0, 60000],
    staleLevel: ['num', 0, 1],
    punch:      ['num', 0, 1]
};

// The "77 inch / 300 LED" preset: the values this TV is calibrated with. Kept in sync with the
// ui.html DEFAULTS and with the working /opt/usr/share/ambilight/config.json. hdr/gamut=0 are
// required for SDR content - the PQ/BT.2020 path shifts saturated hues (orange -> yellow).
var DEFAULTS = {
    outputEnabled: 1,
    ledCount: 300, zoneCount: 300, zoneTop: 97, zoneRight: 53, zoneBottom: 97, zoneLeft: 53,
    mapOffset: 149, mapReverse: 1, bandFrac: 0.08,
    // saturation stays 1: >1 expands chroma BEFORE the BT.601 matrix, which drives low channels
    // negative and clips them to zero (a warm green lost all its red - measured on the TV).
    bars: 1, barRows: 0, barThresh: 18, alpha: 0.25, gamma: 1, saturation: 1, adaptiveGamma: 0,
    black: 2, spatial: 0.15, peak: 400, hdr: 0, gamut: 0,
    targetFps: 25, staleMs: 6000, staleLevel: 0.35, punch: 0
};

var START = [
    'export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1',
    // modprobe is not persistent across a reboot and the CDC device node only exists once the
    // module is loaded.
    'if ! lsmod | grep -q "^cdc_acm"; then modprobe cdc_acm 2>/dev/null; fi',
    'rm -f ' + STOPFILE,
    'if pgrep -f "' + MATCH + '" >/dev/null; then echo "already running"; exit 0; fi',
    'if [ ! -f ' + APP + ' ]; then echo "missing ' + APP + '"; exit 3; fi',
    'mkdir -p /opt/usr/share/ambilight /opt/ambilight',
    'setsid nohup /usr/bin/dotnet ' + APP + ' >> ' + LOG + ' 2>&1 < /dev/null &',
    'sleep 3',
    'if pgrep -f "' + MATCH + '" >/dev/null; then echo "started"; else echo "FAILED to start"; tail -n 5 ' + LOG + '; exit 4; fi'
].join('\n');

var STOP = [
    'if ! pgrep -f "' + MATCH + '" >/dev/null; then echo "not running"; exit 0; fi',
    'touch ' + STOPFILE,
    'i=0; while [ $i -lt 25 ] && pgrep -f "' + MATCH + '" >/dev/null; do i=$((i+1)); sleep 1; done',
    'if pgrep -f "' + MATCH + '" >/dev/null; then echo "STILL RUNNING after 25 s - left alone on purpose"; exit 5; fi',
    'echo "stopped"'
].join('\n');

var STATE = [
    'P=$(pgrep -f "' + MATCH + '" | head -n 1)',
    'if [ -n "$P" ]; then echo "state=running pid=$P"; else echo "state=stopped"; fi',
    'D=$(sed -n "s/.*\\\"serialDevice\\\"[[:space:]]*:[[:space:]]*\\\"\\([^\\\"]*\\)\\\".*/\\1/p" ' + CONFIG + ')',
    'if [ -z "$D" ]; then D=/dev/ttyACM1; fi',
    'if [ -e "$D" ]; then echo "device=$D present"; else echo "device=$D MISSING"; fi',
    'if lsmod | grep -q "^cdc_acm"; then echo "module=cdc_acm loaded"; else echo "module=cdc_acm NOT loaded"; fi',
    'grep "=== " ' + LOG + ' 2>/dev/null | tail -n 1 | sed "s/^/version=/"',
    'grep "serial port open" ' + LOG + ' 2>/dev/null | tail -n 1 | sed "s/^/serial=/"',
    'grep "no video-plane frame" ' + LOG + ' 2>/dev/null | tail -n 1 | sed "s/^/capture_problem=/"'
].join('\n');

function readConfigAsync(ctx, cb) {
    // The Vetro process can read its own plugin folder but NOT /opt/usr/share/... directly (Smack:
    // fs.readFileSync returns EACCES there). ctx.execute runs as root and is the only way through.
    var p = run(ctx, 'cat ' + CONFIG + ' 2>/dev/null', 5000);
    if (!p) { cb({}); return; }
    p.then(function (r) {
        try {
            var c = JSON.parse(String((r && r.stdout) || ''));
            cb(c && typeof c === 'object' ? c : {});
        } catch (e) { cb({}); }
    }, function () { cb({}); });
}

// Returns { clean: {...}, errors: [...] }. Never writes anything itself. `fwLeds` comes from the
// firmware banner, so a config larger than the compiled strip can never be saved from the UI.
function validate(values, fwLeds) {
    var clean = {}, errors = [];
    if (!values || typeof values !== 'object') return { clean: clean, errors: ['no values supplied'] };
    for (var key in SPECS) {
        if (!Object.prototype.hasOwnProperty.call(values, key)) continue;
        var spec = SPECS[key], v = values[key];
        if (spec[0] === 'int') v = Math.floor(Number(v));
        else v = Number(v);
        if (!isFinite(v)) { errors.push(key + ': not a number'); continue; }
        var hi = spec[2];
        if (key === 'ledCount' && fwLeds > 0) hi = Math.min(hi, fwLeds);
        if (key === 'zoneCount') hi = Math.min(hi, Number(clean.ledCount || values.ledCount || 2048));
        if (v < spec[1] || v > hi) { errors.push(key + ': must be ' + spec[1] + '..' + hi); continue; }
        clean[key] = v;
    }
    return { clean: clean, errors: errors };
}

function run(ctx, cmd, ms) {
    try { return ctx.execute(cmd, ms || 20000); }
    catch (e) { return null; }
}

module.exports = function (ctx) {
    var cache = { state: 'unknown', log: [], lastAction: null, lastError: null };

    function remember(text) {
        var lines = String(text || '').split('\n');
        var out = { log: [] };
        for (var i = 0; i < lines.length; i++) {
            var l = lines[i];
            if (/^state=/.test(l)) out.state = l.slice(6);
            else if (/^pid=/.test(l)) out.pid = l.slice(4);
            else if (/^device=/.test(l)) out.device = l.slice(7);
            else if (/^module=/.test(l)) out.module = l.slice(7);
            else if (/^version=/.test(l)) out.version = l.slice(8);
            else if (/^serial=/.test(l)) out.serial = l.slice(7);
            else if (/^capture_problem=/.test(l)) out.captureProblem = l.slice(16);
            else if (l.length) out.log.push(l);
        }
        return out;
    }

    function refresh(cb) {
        var p = run(ctx, STATE, 12000);
        if (!p) { if (cb) cb(); return; }
        p.then(function (r) {
            var s = remember((r && r.stdout) || '');
            if (s.state) cache.state = s.state;
            if (s.device) cache.device = s.device;
            if (s.module) cache.module = s.module;
            if (s.version) cache.version = s.version;
            if (s.serial) cache.serial = s.serial;
            if (s.captureProblem) cache.captureProblem = s.captureProblem;
            if (s.pid) cache.pid = s.pid;
            if (cb) cb();
        }, function (e) { cache.lastError = String(e && e.message || e); if (cb) cb(); });
    }

    // At boot Vetro starts Shards before its root helper is connected. ctx.execute then rejects with
    // "no root agent connected" and the service would never start until someone re-enabled the Shard
    // (measured after a reboot: Shard running, lastAction = start -> error: no root agent connected).
    // Retry that specific condition for up to ~110 s; any other error is reported at once, because
    // retrying a broken command would only spam the shared execute queue.
    var RETRY_DELAYS = [5000, 15000, 30000, 60000, 60000];

    function failed(name, msg, cb) {
        cache.lastError = msg;
        cache.lastAction = name + ' -> error: ' + msg;
        if (cb) cb(null);
    }

    function attempt(cmd, ms, name, idx, cb) {
        var p = run(ctx, cmd, ms);
        if (!p) {
            if (idx < RETRY_DELAYS.length) {
                cache.lastAction = name + ' -> no root agent, retry ' + (idx + 1) + ' in ' +
                                   (RETRY_DELAYS[idx] / 1000) + 's';
                setTimeout(function () { attempt(cmd, ms, name, idx + 1, cb); }, RETRY_DELAYS[idx]);
                return;
            }
            failed(name, 'execute unavailable', cb);
            return;
        }
        p.then(function (r) {
            cache.lastAction = name + ' -> ' + String((r && r.stdout) || '').trim();
            cache.lastError = null;
            refresh(function () { if (cb) cb(r); });
        }, function (e) {
            var msg = String(e && e.message || e);
            if (idx < RETRY_DELAYS.length && /root agent|not connected|no agent/i.test(msg)) {
                cache.lastAction = name + ' -> waiting for root agent, retry ' + (idx + 1) + ' in ' +
                                   (RETRY_DELAYS[idx] / 1000) + 's';
                setTimeout(function () { attempt(cmd, ms, name, idx + 1, cb); }, RETRY_DELAYS[idx]);
                return;
            }
            failed(name, msg, cb);
        });
    }

    function act(cmd, ms, name, cb) {
        attempt(cmd, ms, name, 0, cb);
    }

    function json(res, body, code) {
        res.writeHead(code || 200, { 'content-type': 'application/json', 'cache-control': 'no-store' });
        res.end(JSON.stringify(body));
    }

    function logTail(n, cb) {
        var p = run(ctx, 'tail -n ' + (n || 40) + ' ' + LOG + ' 2>/dev/null', 5000);
        if (!p) { cb('(no log yet)'); return; }
        p.then(function (r) { cb(String((r && r.stdout) || '(no log yet)')); },
               function () { cb('(no log yet)'); });
    }

    function loadPage() {
        var places = [path.join(__dirname, 'ui.html'), path.join(ctx.dir || '', 'ui.html')];
        for (var i = 0; i < places.length; i++) {
            try { return fs.readFileSync(places[i], 'utf8'); } catch (e) {}
        }
        return '<!doctype html><meta charset="utf-8"><p>ui.html missing: reinstall this Shard.</p>';
    }

    // Ask the board itself how many LEDs it was compiled for: a config larger than that silently drops
    // frames. Probing needs the port, so it is only done while the service is stopped; otherwise use the
    // last successful answer kept in the Shard's own config.json.
    function probeFirmware(cb) {
        var saved = ctx.config.get() || {};
        var cached = {
            leds: Number(saved.firmwareLeds) || 300,
            type: saved.firmwareType || 'RGBW',
            source: 'cached'
        };
        readConfigAsync(ctx, function (cfg) {
            probeWithDevice(cfg.serialDevice, cached, cb);
        });
    }

    function probeWithDevice(device, cached, cb) {
        var dev = String(device || '');
        if (!/^[A-Za-z0-9_./\-]+$/.test(dev)) { cb(cached); return; }
        var cmd = [
            'if pgrep -f "' + MATCH + '" >/dev/null; then echo FW_BUSY; exit 0; fi',
            'stty -F "' + dev + '" 115200 raw -echo -icanon -opost -ixon -ixoff min 0 time 30 2>/dev/null',
            'exec 3<>"' + dev + '" || { echo FW_NODEV; exit 0; }',
            // ver 1, type 3 (hello), seq 1, len 0, CRC-16/CCITT-FALSE of 01 03 01 00 00 = 0xD3D6
            'printf "\\245\\132\\001\\003\\001\\000\\000\\000\\326\\323" >&3',
            'dd bs=32 count=1 <&3 2>/dev/null | tr -d "\\000\\015"'
        ].join('\n');
        var p = run(ctx, cmd, 8000);
        if (!p) { cb(cached); return; }
        p.then(function (r) {
            var text = String((r && r.stdout) || '');
            var m = /AMBI1\s+(\d+)\s+(RGBW|RGB)/.exec(text);
            if (!m) {
                cached.note = /FW_BUSY/.test(text) ? 'service running - using last known board value'
                                                   : 'board did not answer - using last known value';
                cb(cached);
                return;
            }
            var live = { leds: Number(m[1]), type: m[2], source: 'firmware' };
            try { ctx.config.set({ firmwareLeds: live.leds, firmwareType: live.type }); } catch (e) {}
            cb(live);
        }, function () { cb(cached); });
    }

    function saveConfig(values, cb) {
        readConfigAsync(ctx, function (cfg) {
            for (var key in values) {
                if (Object.prototype.hasOwnProperty.call(values, key)) cfg[key] = values[key];
            }
            // Same restriction as reads: the Shard cannot write /opt/... itself. Write via the root
            // helper and rename atomically, so the app never sees a half-written config.json.
            var json = JSON.stringify(cfg, null, 2) + '\n';
            var cmd = 'cat > ' + CONFIG + '.tmp <<\'AMBCONFIG\'\n' + json + 'AMBCONFIG\n' +
                      'mv -f ' + CONFIG + '.tmp ' + CONFIG + '\nchmod 666 ' + CONFIG;
            var p = run(ctx, cmd, 10000);
            if (!p) { cb('execute unavailable'); return; }
            p.then(function () { cb(null); }, function (e) { cb(String(e && e.message || e)); });
        });
    }

    function readBody(req, cb) {
        var chunks = [], size = 0, done = false;
        req.on('data', function (c) {
            if (done) return;
            size += c.length;
            if (size > 65536) { done = true; cb(null, new Error('body too large')); return; }
            chunks.push(c);
        });
        req.on('end', function () {
            if (done) return;
            done = true;
            try { cb(JSON.parse(Buffer.concat(chunks).toString('utf8'))); }
            catch (e) { cb(null, e); }
        });
    }

    var page = null;

    return {
        start: function () {
            page = loadPage();
            // Boot path. A failing start must not take the Shard down, so the promise is handled
            // here rather than thrown.
            act(START, 30000, 'start');
        },

        stop: function () {
            page = null;
        },

        status: function () {
            return {
                state: cache.state,
                pid: cache.pid,
                device: cache.device,
                module: cache.module,
                version: cache.version,
                lastAction: cache.lastAction,
                lastError: cache.lastError || undefined
            };
        },

        route: function (req, res, sub) {
            if (sub === 'ui' && req.method === 'GET') {
                res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
                res.end(page || loadPage());
                return true;
            }
            if (sub === 'state' && req.method === 'GET') {
                refresh(function () {
                    logTail(25, function (log) {
                        json(res, { state: cache.state, pid: cache.pid, device: cache.device,
                            module: cache.module, version: cache.version, serial: cache.serial,
                            captureProblem: cache.captureProblem, lastAction: cache.lastAction,
                            lastError: cache.lastError, log: log });
                    });
                });
                return true;
            }
            if (sub === 'output' && req.method === 'POST') {
                readBody(req, function (body, err) {
                    if (err) { json(res, { ok: false, error: err.message }, 400); return; }
                    var v = Number(body && body.enabled);
                    if (v !== 0 && v !== 1) {
                        json(res, { ok: false, errors: ['enabled must be 0 or 1'] }, 400);
                        return;
                    }
                    // Live: the app polls /tmp/amb-control when no controlUrl is configured. The
                    // SCORPIO blanks the strip ~400 ms after the last frame, so no restart is needed.
                    var p = run(ctx, 'printf "enable=' + v + '\\n" > /tmp/amb-control', 5000);
                    if (!p) { json(res, { ok: false, error: 'execute unavailable' }, 500); return; }
                    p.then(function () {
                        // Persist for the next service start as well (config is read only at startup).
                        saveConfig({ outputEnabled: v }, function (saveErr) {
                            if (saveErr) { json(res, { ok: false, error: saveErr }, 500); return; }
                            json(res, { ok: true, enabled: v });
                        });
                    }, function (e) { json(res, { ok: false, error: String(e && e.message || e) }, 500); });
                });
                return true;
            }
            if (sub === 'config' && req.method === 'GET') {
                readConfigAsync(ctx, function (cfg) {
                    probeFirmware(function (fw) {
                        json(res, { config: cfg, firmware: fw, defaults: DEFAULTS, specs: SPECS });
                    });
                });
                return true;
            }
            if (sub === 'config' && req.method === 'POST') {
                readBody(req, function (body, err) {
                    if (err) { json(res, { ok: false, error: err.message }, 400); return; }
                    probeFirmware(function (fw) {
                        var checked = validate(body && body.values, fw.leds);
                        if (checked.errors.length) { json(res, { ok: false, errors: checked.errors }, 400); return; }
                        saveConfig(checked.clean, function (saveErr) {
                            if (saveErr) { json(res, { ok: false, error: saveErr }, 500); return; }
                            var done = function () {
                                json(res, { ok: true, values: checked.clean, firmware: fw,
                                            restarted: !!(body && body.restart) });
                            };
                            if (body && body.restart) {
                                act(STOP, 40000, 'stop', function () {
                                    act(START, 30000, 'start', done);
                                });
                            } else done();
                        });
                    });
                });
                return true;
            }
            if (req.method !== 'POST') return false;
            if (sub === 'start') { act(START, 30000, 'start', function () { json(res, { ok: true, state: cache.state, lastAction: cache.lastAction }); }); return true; }
            if (sub === 'stop') { act(STOP, 40000, 'stop', function () { json(res, { ok: true, state: cache.state, lastAction: cache.lastAction }); }); return true; }
            if (sub === 'restart') {
                act(STOP, 40000, 'stop', function () {
                    act(START, 30000, 'start', function () { json(res, { ok: true, state: cache.state, lastAction: cache.lastAction }); });
                });
                return true;
            }
            return false;
        }
    };
};
