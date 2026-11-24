#!/usr/bin/env python3
"""Chance control: for each clip in a pred dir, write the same NUMBER of anchors at seeded uniformly random frames.
Usage: control.py --labels <dir> --pred <dir> --out <dir> [--seed 7]"""
import argparse, json, os, random
ap = argparse.ArgumentParser(); ap.add_argument("--labels"); ap.add_argument("--pred"); ap.add_argument("--out"); ap.add_argument("--seed", type=int, default=7)
a = ap.parse_args(); os.makedirs(a.out, exist_ok=True)
for fn in sorted(os.listdir(a.pred)):
    if not fn.endswith(".pred.json"): continue
    p = json.load(open(os.path.join(a.pred, fn)))
    lab = json.load(open(os.path.join(a.labels, p["clip"] + ".labels.json")))
    r = random.Random(f"{a.seed}-{p['clip']}")
    fr = sorted(r.randrange(1, lab["frames"] - 1) for _ in p["anchors"])
    json.dump({"clip": p["clip"], "fps": lab["fps"], "anchors": [{"id": f"c{i}", "frame": f} for i, f in enumerate(fr)]}, open(os.path.join(a.out, fn), "w"))
