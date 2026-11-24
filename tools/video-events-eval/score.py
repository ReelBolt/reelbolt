#!/usr/bin/env python3
"""Score predicted event anchors against labelled events.

Usage: score.py --labels <dir-of-label-json> --pred <dir-of-pred-json> [--tol 2] [--out report.json] [--md report.md]

Label file  <clip>.labels.json : {"clip","videoType","fps","frames","events":[{"id","type","frame",...}]}
Pred file   <clip>.pred.json   : {"clip","fps","anchors":[{"id","frame","type"?}]}   (type absent = untyped)

Matching: one-to-one, nearest-first, |frame diff| <= tol. Two scores:
  typed   - a prediction matches only a label of the same type (predictions with no type never match)
  untyped - type ignored: "does an anchor exist within +/-tol of a real event" (the only fair score for
            today's analyzer, whose anchors carry no event type)
Per clip, rolled up per event type and per videoType (micro-averaged: counts are summed, then P/R/F1).
Stdlib only.
"""
import argparse, json, os, sys
from collections import defaultdict


def match(truth, preds, tol, typed):
    """Greedy one-to-one nearest-first. Returns (tp, fp, fn, matched_truth_idx, errs)."""
    cands = []
    for i, t in enumerate(truth):
        for j, p in enumerate(preds):
            if typed and (p.get("type") is None or p["type"] != t["type"]):
                continue
            d = abs(p["frame"] - t["frame"])
            if d <= tol:
                cands.append((d, i, j))
    cands.sort()
    ut, up, pairs = set(), set(), []
    for d, i, j in cands:
        if i in ut or j in up:
            continue
        ut.add(i); up.add(j); pairs.append((i, j, d))
    return pairs, ut, up


class Counts:
    def __init__(self): self.tp = self.fp = self.fn = 0
    def add(self, tp, fp, fn): self.tp += tp; self.fp += fp; self.fn += fn
    def row(self):
        p = self.tp / (self.tp + self.fp) if self.tp + self.fp else None
        r = self.tp / (self.tp + self.fn) if self.tp + self.fn else None
        f = 2 * p * r / (p + r) if p and r else (0.0 if p is not None and r is not None else None)
        return {"tp": self.tp, "fp": self.fp, "fn": self.fn,
                "precision": p, "recall": r, "f1": f}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--labels", required=True)
    ap.add_argument("--pred", required=True)
    ap.add_argument("--tol", type=int, default=2)
    ap.add_argument("--out"); ap.add_argument("--md")
    ap.add_argument("--name", default="run")
    a = ap.parse_args()

    res = {m: {"by_type": defaultdict(Counts), "by_video": defaultdict(Counts),
               "by_video_type": defaultdict(Counts), "all": Counts()} for m in ("typed", "untyped")}
    missing = []
    no_beat = {}  # clips labelled expect.noBeats (speech only): predicted beat count, must be 0
    for fn in sorted(os.listdir(a.labels)):
        if not fn.endswith(".labels.json"): continue
        lab = json.load(open(os.path.join(a.labels, fn)))
        clip = lab["clip"]; vt = lab["videoType"]
        pp = os.path.join(a.pred, clip + ".pred.json")
        if not os.path.exists(pp):
            missing.append(clip)  # counted: every label of the clip is a miss
            pred = {"anchors": []}
        else:
            pred = json.load(open(pp))
        truth = lab["events"]; anchors = pred["anchors"]
        if (lab.get("expect") or {}).get("noBeats"):
            no_beat[clip] = sum(1 for p in anchors if p.get("type") == "beat")
        for mode in ("typed", "untyped"):
            typed = mode == "typed"
            pairs, ut, up = match(truth, anchors, a.tol, typed)
            R = res[mode]
            # recall side per truth type
            for i, t in enumerate(truth):
                hit = i in ut
                R["by_type"][t["type"]].add(1 if hit else 0, 0, 0 if hit else 1)
                R["by_video_type"][(vt, t["type"])].add(1 if hit else 0, 0, 0 if hit else 1)
                R["by_video"][vt].add(1 if hit else 0, 0, 0 if hit else 1)
                R["all"].add(1 if hit else 0, 0, 0 if hit else 1)
            # precision side: unmatched predictions are FPs; typed FP is credited to the predicted type
            for j, p in enumerate(anchors):
                if j in up: continue
                ty = p.get("type") or "(untyped)"
                R["by_type"][ty].add(0, 1, 0)
                R["by_video_type"][(vt, ty)].add(0, 1, 0)
                R["by_video"][vt].add(0, 1, 0)
                R["all"].add(0, 1, 0)
            # a matched prediction counts toward precision tp already (via the truth side tp); note: tp is shared.
    out = {"name": a.name, "tolFrames": a.tol, "clipsMissingPred": missing, "noBeatClips": no_beat}
    for mode, R in res.items():
        out[mode] = {
            "all": R["all"].row(),
            "byVideoType": {k: v.row() for k, v in sorted(R["by_video"].items())},
            "byType": {k: v.row() for k, v in sorted(R["by_type"].items())},
            "byVideoTypeAndType": {f"{k[0]}/{k[1]}": v.row() for k, v in sorted(R["by_video_type"].items())},
        }
    if a.out: json.dump(out, open(a.out, "w"), indent=2)
    md = render(out)
    if a.md: open(a.md, "w").write(md)
    print(md)


def fmt(x): return "n/a" if x is None else f"{x:.2f}"


def render(out):
    lines = [f"# Event score: {out['name']} (tolerance +/-{out['tolFrames']} frames)", ""]
    if out["clipsMissingPred"]: lines += [f"Clips with no prediction file: {', '.join(out['clipsMissingPred'])}", ""]
    if out.get("noBeatClips"):
        lines += ["Beats predicted on speech-only clips (expect.noBeats): " + ", ".join(f"{c}={n}" for c, n in out["noBeatClips"].items()), ""]
    for mode in ("untyped", "typed"):
        m = out[mode]
        lines += [f"## {mode}", "", "| scope | TP | FP | FN | P | R | F1 |", "|---|---|---|---|---|---|---|"]
        def row(name, r): lines.append(f"| {name} | {r['tp']} | {r['fp']} | {r['fn']} | {fmt(r['precision'])} | {fmt(r['recall'])} | {fmt(r['f1'])} |")
        row("ALL", m["all"])
        for k, v in m["byVideoType"].items(): row(f"video: {k}", v)
        for k, v in m["byType"].items(): row(f"type: {k}", v)
        lines.append("")
    lines += ["## video type x event type (untyped recall is the meaningful column)", "",
              "| scope | TP | FP | FN | P | R |", "|---|---|---|---|---|---|"]
    for k, v in out["untyped"]["byVideoTypeAndType"].items():
        lines.append(f"| {k} | {v['tp']} | {v['fp']} | {v['fn']} | {fmt(v['precision'])} | {fmt(v['recall'])} |")
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    sys.exit(main())
