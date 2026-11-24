#!/usr/bin/env python3
"""prepare.py <clip.mp4> [--candidates pred.json] [--out DIR]
Extract every frame as a 480px JPEG into ${VEA_DATA:-~/ve-data}/real/frames/<id>/%05d.jpg
and write <id>/manifest.json (and candidates.json when --candidates is given)."""
import argparse, csv, json, os, subprocess, sys

def probe(p):
    o = subprocess.check_output(["ffprobe", "-v", "error", "-select_streams", "v:0", "-count_frames",
        "-show_entries", "stream=width,height,r_frame_rate,nb_read_frames", "-of", "json", p])
    s = json.loads(o)["streams"][0]
    n, d = s["r_frame_rate"].split("/")
    return float(n) / float(d), int(s["nb_read_frames"]), s["width"], s["height"]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("clip"); ap.add_argument("--candidates"); ap.add_argument("--out")
    a = ap.parse_args()
    cid = os.path.splitext(os.path.basename(a.clip))[0]
    root = os.path.join(os.environ.get("VEA_DATA", os.path.expanduser("~/ve-data")), "real")
    out = a.out or os.path.join(root, "frames", cid)
    os.makedirs(out, exist_ok=True)
    fps, frames, w, h = probe(a.clip)
    subprocess.check_call(["ffmpeg", "-nostdin", "-y", "-loglevel", "error", "-i", a.clip, "-vf", "scale=480:-2",
        "-q:v", "4", "-fps_mode", "passthrough", os.path.join(out, "%05d.jpg")])
    vt = "unknown"
    mf = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "manifest.csv")
    if os.path.exists(mf):
        for r in csv.DictReader(open(mf, encoding="utf-8")):
            if r["id"] == cid: vt = r["videoType"]
    man = {"clip": cid, "videoType": vt, "source": "real", "fps": fps, "frames": frames,
           "width": w, "height": h, "firstFrameIndex": 1, "pattern": "%05d.jpg"}
    json.dump(man, open(os.path.join(out, "manifest.json"), "w"), indent=1)
    if a.candidates:
        pj = json.load(open(a.candidates))
        c = [{"frame": int(x["frame"]), "type": x.get("type")} for x in pj.get("anchors", [])]
        json.dump({"clip": cid, "candidates": c}, open(os.path.join(out, "candidates.json"), "w"), indent=1)
    print(f"{cid}: {frames} frames @ {fps:.3f} fps -> {out}")

if __name__ == "__main__":
    sys.exit(main())
