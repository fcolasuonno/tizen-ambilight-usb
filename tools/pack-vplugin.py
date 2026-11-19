#!/usr/bin/env python3
"""Pack a Vetro Shard folder into a .vplugin bundle.

A bundle is one text file (Vetro's own `vetro-plugin.cjs pack` makes the same thing):

    { "format": "vetro-plugin", "version": 1, "manifest": {…}, "files": { "index.js": "…", … } }

Rules the TV enforces on install (`checkBundle` in plugin-host.js): text only, 1-20 files, 512 KB in
total, file names `a-z A-Z 0-9 . _ -` and `/`, `manifest.json` is NOT a file entry (the TV writes it from
the manifest itself), and the entry file named by `manifest.main` must be present. `config.json`,
dotfiles and `node_modules` are not packed - the TV keeps `config.json` as the plugin's saved settings.

    python3 tools/pack-vplugin.py vetro-plugin/ambilight
    python3 tools/pack-vplugin.py vetro-plugin/ambilight --print-sha256
"""
import argparse, hashlib, json, os, re, sys

FILE_RE = re.compile(r"^[A-Za-z0-9_-][A-Za-z0-9._-]*(\/[A-Za-z0-9_-][A-Za-z0-9._-]*)*$")
SKIP = {"manifest.json", "config.json"}
BUNDLE_MAX_FILES = 20
BUNDLE_MAX_BYTES = 512 * 1024


def collect(folder):
    files = {}
    for root, dirs, names in os.walk(folder):
        dirs[:] = [d for d in dirs if not d.startswith(".") and d != "node_modules"]
        for n in sorted(names):
            if n.startswith(".") or n in SKIP:
                continue
            full = os.path.join(root, n)
            rel = os.path.relpath(full, folder).replace(os.sep, "/")
            with open(full, "r", encoding="utf-8") as f:
                files[rel] = f.read()
    return files


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("-o", "--out", default=None)
    ap.add_argument("--print-sha256", action="store_true", help="only print the hash of an existing bundle")
    args = ap.parse_args()

    with open(os.path.join(args.folder, "manifest.json"), "r", encoding="utf-8") as f:
        manifest = json.load(f)

    files = collect(args.folder)
    main_file = manifest.get("main") or "index.js"
    if main_file not in files:
        sys.exit("entry file %r is missing" % main_file)
    if len(files) > BUNDLE_MAX_FILES:
        sys.exit("%d files, the TV accepts at most %d" % (len(files), BUNDLE_MAX_FILES))
    for name in files:
        if not FILE_RE.match(name):
            sys.exit("invalid file name for a bundle: %r" % name)
    total = sum(len(v.encode("utf-8")) for v in files.values())
    if total > BUNDLE_MAX_BYTES:
        sys.exit("%d bytes, the TV accepts at most %d" % (total, BUNDLE_MAX_BYTES))

    bundle = {"format": "vetro-plugin", "version": 1, "manifest": manifest, "files": files}
    text = json.dumps(bundle, ensure_ascii=False, separators=(",", ":"))

    out = args.out or os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(args.folder))),
        "dist", "%s-%s.vplugin" % (manifest["id"], manifest.get("version") or "0.0.0"))
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        f.write(text)

    sha = hashlib.sha256(text.encode("utf-8")).hexdigest()
    print("%s  %s" % (out, out and ""))
    print("sha256 %s" % sha)
    print("%d files, %d bytes, entry %s" % (len(files), len(text.encode("utf-8")), main_file))


if __name__ == "__main__":
    main()
