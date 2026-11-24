#!/usr/bin/env python3
"""Regenerates sandbox/fonts/ (the baked set), fonts.json and the Google Fonts catalog.

Workstation-only maintenance script: it reads a checkout of github.com/google/fonts at the ONE pinned
commit below (clone it OUTSIDE this repo) and writes deterministic output. Nothing here runs in CI,
in tests or in the image build.

    git init gf && cd gf && git remote add origin https://github.com/google/fonts.git
    git fetch --depth 1 origin 47d54ca1ebb14fd864695f1d39dc734f39017c33 && git checkout FETCH_HEAD
    python3 -I sandbox/scripts/generate-fonts.py /path/to/gf

Outputs: sandbox/fonts/<Family>/{files,OFL.txt}, sandbox/fonts/fonts.json, and
inference/src/ReelBolt.Shared/DesignSystems/Data/google-fonts-catalog.json (OFL/Apache/UFL only).
"""
import hashlib, json, os, re, shutil, subprocess, sys

COMMIT = "47d54ca1ebb14fd864695f1d39dc734f39017c33"
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FONTS_DIR = os.path.join(ROOT, "sandbox", "fonts")
CATALOG = os.path.join(ROOT, "inference", "src", "ReelBolt.Shared", "DesignSystems", "Data", "google-fonts-catalog.json")
ALLOWED = {"OFL": "ofl", "APACHE2": "apache", "UFL": "ufl"}

# Baked set, in manifest order: (google/fonts directory under ofl/, our category).
BAKED = [
    ("inter", "sans"), ("montserrat", "sans"), ("poppins", "sans"), ("dmsans", "sans"), ("spacegrotesk", "sans"),
    ("chakrapetch", "sans"), ("bebasneue", "display"), ("anton", "display"), ("playfairdisplay", "serif"),
    ("dmserifdisplay", "serif"), ("jetbrainsmono", "mono"),
    ("roboto", "sans"), ("opensans", "sans"), ("oswald", "sans"), ("raleway", "sans"), ("sourcesans3", "sans"),
    ("nunito", "sans"), ("worksans", "sans"), ("rubik", "sans"), ("barlow", "sans"), ("barlowcondensed", "sans"),
    ("archivo", "sans"), ("manrope", "sans"), ("outfit", "sans"), ("plusjakartasans", "sans"), ("ibmplexsans", "sans"),
    ("lora", "serif"), ("librebaskerville", "serif"), ("cormorantgaramond", "serif"), ("ebgaramond", "serif"),
    ("ibmplexmono", "mono"), ("firacode", "mono"), ("robotomono", "mono"), ("spacemono", "mono"),
    ("abrilfatface", "display"), ("lobster", "display"), ("pacifico", "display"), ("caveat", "display"),
    ("dancingscript", "display"), ("archivoblack", "display"), ("alfaslabone", "display"),
]
STATIC_NORMAL = {400, 600, 700}   # Regular, SemiBold, Bold
STATIC_ITALIC = {400, 700}        # Italic, Bold Italic
CATEGORY = {"SANS_SERIF": "sans", "SERIF": "serif", "MONOSPACE": "mono", "DISPLAY": "display", "HANDWRITING": "handwriting"}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def parse_metadata(path):
    text = open(path, encoding="utf-8").read()
    name = re.search(r'^name: "([^"]+)"', text, re.M).group(1)
    lic = re.search(r'^license: "([^"]+)"', text, re.M).group(1)
    cat = re.search(r'^category: "([^"]+)"', text, re.M)
    fonts = []
    for block in re.findall(r"^fonts \{(.*?)^\}", text, re.M | re.S):
        fonts.append((re.search(r'style: "(\w+)"', block).group(1), int(re.search(r"weight: (\d+)", block).group(1)),
                      re.search(r'filename: "([^"]+)"', block).group(1)))
    wght = re.search(r'axes \{\s*tag: "wght"\s*min_value: ([\d.]+)\s*max_value: ([\d.]+)', text)
    rng = (int(float(wght.group(1))), int(float(wght.group(2)))) if wght else None
    return name, lic, (CATEGORY.get(cat.group(1), cat.group(1).lower()) if cat else "display"), fonts, rng


def main(gf):
    head = subprocess.run(["git", "-C", gf, "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    if head != COMMIT:
        sys.exit(f"{gf} is at {head}, expected the pinned commit {COMMIT}")

    # ---- baked set
    manifest = []
    for dirname, category in BAKED:
        src = os.path.join(gf, "ofl", dirname)
        name, lic, _, fonts, rng = parse_metadata(os.path.join(src, "METADATA.pb"))
        assert lic == "OFL", (name, lic)
        out = os.path.join(FONTS_DIR, name)
        os.makedirs(out, exist_ok=True)
        chosen = []
        for style, weight, fn in fonts:
            variable = "[" in fn
            if variable or weight in (STATIC_NORMAL if style == "normal" else STATIC_ITALIC):
                chosen.append(fn)
        files = []
        for fn in sorted(set(chosen)):
            shutil.copyfile(os.path.join(src, fn), os.path.join(out, fn))
            files.append({"path": f"{name}/{fn}", "sha256": sha256(os.path.join(out, fn))})
        shutil.copyfile(os.path.join(src, "OFL.txt"), os.path.join(out, "OFL.txt"))
        manifest.append({"family": name, "category": category, "files": files})
    with open(os.path.join(FONTS_DIR, "fonts.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")

    # ---- catalog
    families = []
    for lic_dir in ("ofl", "apache", "ufl"):
        base = os.path.join(gf, lic_dir)
        if not os.path.isdir(base):
            continue
        for d in sorted(os.listdir(base)):
            if not re.fullmatch(r"[a-z0-9]{1,64}", d):  # the engine's catalog loader only accepts these directory names (skips *_todelist)
                continue
            meta = os.path.join(base, d, "METADATA.pb")
            if not os.path.isfile(meta):
                continue
            name, lic, cat, fonts, rng = parse_metadata(meta)
            if ALLOWED.get(lic) != lic_dir or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9 ]{0,63}", name):
                continue
            entries, seen = [], set()
            for style, weight, fn in fonts:
                if fn in seen or not re.fullmatch(r"[A-Za-z0-9\[\],_.\-]+\.(ttf|otf)", fn):
                    continue
                p = os.path.join(base, d, fn)
                if not os.path.isfile(p):
                    continue
                seen.add(fn)
                var = "[" in fn and "wght" in fn and rng is not None
                entries.append({"file": fn, "style": style, "weight": rng[0] if var else weight,
                                "weightMax": rng[1] if var else weight, "size": os.path.getsize(p), "sha256": sha256(p)})
            if entries:
                families.append({"family": name, "license": lic, "category": cat, "dir": f"{lic_dir}/{d}", "files": entries})
    families.sort(key=lambda x: x["family"].lower())
    os.makedirs(os.path.dirname(CATALOG), exist_ok=True)
    with open(CATALOG, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"repository": "google/fonts", "commit": COMMIT, "families": families}, f, separators=(",", ":"), ensure_ascii=False)
        f.write("\n")
    print(f"baked {len(manifest)} families; catalog {len(families)} families, {os.path.getsize(CATALOG)} bytes")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
