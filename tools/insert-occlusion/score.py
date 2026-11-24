"""Scores a harness run over the synthetic clips against their ground truth.

usage: score.py <clips dir> <label> [name ...]
Reads track-{label}-{name}.csv (emitted corners) and composite-{label}-{name}.mp4 (the composited
coordinate card), writes scores-{label}.json and prints one row per clip.

geometry   corner error in px against the projected plate corners; "hit" = a corner the occluder
           covers, or one next to an edge it covers by more than 5%.
registration  where the track's surface model (corner pin, plus edge bows for a meshed track) puts
           a 5x5 lattice of plate points, against where the true surface projects them, in px;
           "occl" = the frames in which something covers a corner or more than 5% of an edge.
uncovered  share of the visible plate still raw green in the composite.
bleed      share of the occluder (inside the plate's box) that the composite painted over.
"""
import csv, json, os, subprocess, sys
import numpy as np
import cv2
import gen

CARD_LOW, CARD_SPAN = 16.0, 223.0


def frames_of(path):
    p = subprocess.Popen(["ffmpeg", "-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], stdout=subprocess.PIPE)
    n = gen.W_OUT * gen.H_OUT * 3
    while True:
        buf = p.stdout.read(n)
        if len(buf) < n:
            break
        yield np.frombuffer(buf, np.uint8).reshape(gen.H_OUT, gen.W_OUT, 3).astype(np.float32)
    p.wait()


def pct(v, q):
    return float(np.percentile(v, q)) if len(v) else float("nan")


def score_clip(d, label, name):
    truth = json.load(open(os.path.join(d, name + ".json")))["frames"]
    n = len(truth)
    out = {"name": name}

    # --- geometry, from the emitted track
    rows = list(csv.DictReader(open(os.path.join(d, f"track-{label}-{name}.csv"))))
    step = n / max(1, len(rows))
    all_err, hit_err, tracked = [], [], 0
    adjacent = {0: ("top", "left"), 1: ("top", "right"), 2: ("bottom", "left"), 3: ("bottom", "right")}
    for r in rows:
        if not r["t_x0"]:
            continue
        tracked += 1
        f = min(n - 1, int(round(int(r["frame"]) * step)))
        g = truth[f]
        for c in range(4):
            e = float(np.hypot(float(r[f"t_x{c}"]) - g["corners"][c][0], float(r[f"t_y{c}"]) - g["corners"][c][1]))
            all_err.append(e)
            if g["cornerCovered"][c] or any(g["edgeCovered"][k] > 0.05 for k in adjacent[c]):
                hit_err.append(e)
    out["trackedPct"] = 100.0 * tracked / max(1, len(rows))
    out["corner"] = {"median": pct(all_err, 50), "p95": pct(all_err, 95), "max": max(all_err, default=float("nan"))}
    out["cornerHit"] = {"n": len(hit_err), "median": pct(hit_err, 50), "p95": pct(hit_err, 95), "max": max(hit_err, default=float("nan"))}

    # --- registration: where the track's surface model puts a 5x5 lattice of plate points,
    #     against where the true surface projects them
    gpath = os.path.join(d, f"grid-{label}-{name}.csv")
    reg_all, reg_occ = [], []
    if os.path.exists(gpath):
        motion, surface, _ = name.split("-")
        for r in csv.DictReader(open(gpath)):
            f = min(n - 1, int(round(int(r["frame"]) * step)))
            g = truth[f]
            Rm, t = gen.pose(motion, surface, f)
            occluded = any(g["cornerCovered"]) or any(v > 0.05 for v in g["edgeCovered"].values())
            for p in range(25):
                gx, gy = gen.project(surface, Rm, t, (p % 5) / 4.0, (p // 5) / 4.0)
                e = float(np.hypot(float(r[f"x{p}"]) - gx, float(r[f"y{p}"]) - gy))
                reg_all.append(e)
                if occluded:
                    reg_occ.append(e)
    out["registration"] = {"median": pct(reg_all, 50), "p95": pct(reg_all, 95), "max": max(reg_all, default=float("nan"))}
    out["registrationOccluded"] = {"n": len(reg_occ) // 25, "median": pct(reg_occ, 50), "p95": pct(reg_occ, 95), "max": max(reg_occ, default=float("nan"))}

    # --- composite, against the per-pixel truth
    comp = os.path.join(d, f"composite-{label}-{name}.mp4")
    if os.path.exists(comp):
        unc, bleed = [], []
        kernel = np.ones((7, 7), np.uint8)
        for i, (src, cmp_) in enumerate(zip(frames_of(os.path.join(d, name + ".mp4")), frames_of(comp))):
            g = gen.frame_truth(name, i, with_maps=True)
            visible = cv2.erode((g["plate"] & (g["occ"] < 0.02)).astype(np.uint8), kernel).astype(bool)
            if visible.sum() < 500:
                continue
            R, G, B = cmp_[..., 0], cmp_[..., 1], cmp_[..., 2]
            green = (G - np.maximum(R, B)) > 40
            unc.append(100.0 * (green & visible).sum() / visible.sum())
            c = np.array(g["corners"])
            x0, y0 = np.floor(c.min(axis=0)).astype(int)
            x1, y1 = np.ceil(c.max(axis=0)).astype(int)
            box = np.zeros_like(visible)
            box[max(0, y0):max(0, y1), max(0, x0):max(0, x1)] = True
            solid = box & (g["occ"] > 0.98)
            if solid.sum() > 200:
                painted = np.abs(cmp_ - src).max(axis=2) > 36
                bleed.append(100.0 * (painted & solid).sum() / solid.sum())
        out["uncoveredPct"] = {"mean": float(np.mean(unc)) if unc else float("nan"), "max": max(unc, default=float("nan")),
                               "framesOver2": int(sum(u > 2 for u in unc))}
        out["bleedPct"] = {"mean": float(np.mean(bleed)) if bleed else 0.0, "max": max(bleed, default=0.0)}
    return out


def fmt(o):
    c, h, r, ro = o["corner"], o["cornerHit"], o["registration"], o["registrationOccluded"]
    s = (f"{o['name']:<28} trk {o['trackedPct']:5.1f}%  corner med/p95/max {c['median']:5.1f} {c['p95']:6.1f} {c['max']:6.1f}"
         f"  hit(n={h['n']:3d}) {h['median']:5.1f} {h['p95']:6.1f} {h['max']:6.1f}"
         f"  reg {r['median']:5.1f} {r['p95']:6.1f} {r['max']:6.1f}  reg-occl(f={ro['n']:2d}) {ro['median']:5.1f} {ro['p95']:6.1f} {ro['max']:6.1f}")
    if "uncoveredPct" in o:
        u, b = o["uncoveredPct"], o["bleedPct"]
        s += f"  uncov mean/max {u['mean']:5.1f} {u['max']:5.1f} (>2%: {u['framesOver2']:2d})  bleed {b['mean']:4.1f} {b['max']:5.1f}"
    return s


if __name__ == "__main__":
    d, label = sys.argv[1], sys.argv[2]
    names = sys.argv[3:] or [n for n in gen.scene_names() if os.path.exists(os.path.join(d, f"track-{label}-{n}.csv"))]
    results = []
    for n in names:
        r = score_clip(d, label, n)
        results.append(r)
        print(fmt(r), flush=True)
    json.dump(results, open(os.path.join(d, f"scores-{label}.json"), "w"), indent=1)
