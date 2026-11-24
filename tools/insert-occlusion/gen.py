"""Synthetic green-screen clips with exact ground truth, for the insert-occlusion work.

A device (dark bezel around a green plate) is a FLAT rectangle or a CYLINDER section in 3D, seen by
a pinhole camera whose pose changes per frame. Occluders are drawn in image space relative to the
plate's projected corners, so what they cover is exact whatever the camera does.

Scene name = {motion}-{surface}-{occlusion}. Continuous pixel coordinates: pixel j spans [j, j+1).
"""
import json, math, os, subprocess, sys, zlib
import numpy as np
import cv2

W_OUT, H_OUT, FPS, SECONDS = 1280, 720, 24, 4
FRAMES = FPS * SECONDS
PLATE_W, PLATE_H, BEZEL = 1.6, 0.9, 0.045
FOCAL = 1100.0
CYL_R = 1.8          # cylinder radius; arc = PLATE_W / R = 0.89 rad, sagitta 11% of the width
SS = 2               # supersampling

MOTIONS = ["static", "pan2d", "tilt3d", "handheld"]
SURFACES = ["flat", "curved"]
OCCLUSIONS = ["clean", "cornertouch", "cornercover", "edgecover", "twocorners", "split"]


def scene_names():
    return [f"{m}-{s}-{o}" for m in MOTIONS for s in SURFACES for o in OCCLUSIONS]


def smooth(x):
    x = min(1.0, max(0.0, x))
    return x * x * (3 - 2 * x)


def pose(motion, surface, i):
    """(R 3x3, t 3) object->camera for frame i."""
    tau = i / (FRAMES - 1)
    t_sec = i / FPS
    yaw = pitch = roll = 0.0
    tx = ty = 0.0
    dist = 2.6
    if motion == "static":
        yaw, pitch = 8, -5
    elif motion == "pan2d":
        tx = 0.35 * math.sin(2 * math.pi * tau)
        ty = 0.12 * math.sin(4 * math.pi * tau + 0.6)
        dist = 3.1 - 0.7 * smooth(tau)
        roll = 3 * math.sin(2 * math.pi * tau + 1.0)
    elif motion == "tilt3d":
        yaw = -30 + 60 * smooth(tau)
        pitch = -6 + 10 * math.sin(2 * math.pi * tau)
        roll = 4 * math.sin(2 * math.pi * tau + 0.5)
        tx = 0.15 * math.sin(2 * math.pi * tau + 2.0)
        dist = 2.3
    elif motion == "handheld":
        yaw = 12 * math.sin(2 * math.pi * 0.35 * t_sec) + 0.5 * math.sin(2 * math.pi * 3.1 * t_sec) + 0.3 * math.sin(2 * math.pi * 5.3 * t_sec + 1)
        pitch = -8 + 7 * math.sin(2 * math.pi * 0.27 * t_sec + 1.3) + 0.4 * math.sin(2 * math.pi * 3.7 * t_sec + 2)
        roll = 5 * math.sin(2 * math.pi * 0.21 * t_sec + 0.4) + 0.3 * math.sin(2 * math.pi * 4.3 * t_sec)
        tx = 0.22 * math.sin(2 * math.pi * 0.3 * t_sec + 0.7) + 0.006 * math.sin(2 * math.pi * 4.1 * t_sec)
        ty = 0.10 * math.sin(2 * math.pi * 0.23 * t_sec) + 0.006 * math.sin(2 * math.pi * 3.3 * t_sec + 1)
        dist = 2.5 + 0.25 * math.sin(2 * math.pi * 0.19 * t_sec)
    if surface == "curved":
        pitch -= 9      # look down on it, so the top and bottom edges show their bow
    y, p, r = (math.radians(a) for a in (yaw, pitch, roll))
    Ry = np.array([[math.cos(y), 0, math.sin(y)], [0, 1, 0], [-math.sin(y), 0, math.cos(y)]])
    Rx = np.array([[1, 0, 0], [0, math.cos(p), -math.sin(p)], [0, math.sin(p), math.cos(p)]])
    Rz = np.array([[math.cos(r), -math.sin(r), 0], [math.sin(r), math.cos(r), 0], [0, 0, 1]])
    return Rz @ Rx @ Ry, np.array([tx, ty, dist])


def surface_point(surface, s, yv):
    """Object-space point of surface coordinates (s = arc length along the width, yv = height)."""
    if surface == "flat":
        return np.array([s, yv, 0.0])
    phi = s / CYL_R
    return np.array([CYL_R * math.sin(phi), yv, -CYL_R * (1 - math.cos(phi))])   # edges toward the camera


def project(surface, R, t, u, v):
    X = R @ surface_point(surface, (u - 0.5) * PLATE_W, (v - 0.5) * PLATE_H) + t
    return (FOCAL * X[0] / X[2] + W_OUT / 2, FOCAL * X[1] / X[2] + H_OUT / 2)


def raycast(surface, R, t, scale):
    """Per pixel at `scale`x resolution: surface coordinates (s, yv) and a validity mask."""
    w, h = W_OUT * scale, H_OUT * scale
    xs = (np.arange(w, dtype=np.float64) + 0.5) / scale
    ys = (np.arange(h, dtype=np.float64) + 0.5) / scale
    gx, gy = np.meshgrid(xs, ys)
    d_c = np.stack([(gx - W_OUT / 2) / FOCAL, (gy - H_OUT / 2) / FOCAL, np.ones_like(gx)], axis=-1)
    d = d_c @ R                      # R^T d_c, row-vector form
    o = -(R.T @ t)
    if surface == "flat":
        lam = -o[2] / d[..., 2]
        P = o + lam[..., None] * d
        return P[..., 0], P[..., 1], lam > 0
    zc = -CYL_R
    a = d[..., 0] ** 2 + d[..., 2] ** 2
    b = 2 * (o[0] * d[..., 0] + (o[2] - zc) * d[..., 2])
    c = o[0] ** 2 + (o[2] - zc) ** 2 - CYL_R ** 2
    disc = b * b - 4 * a * c
    ok = disc > 0
    lam = (-b + np.sqrt(np.where(ok, disc, 0))) / (2 * a)      # far side of the cylinder = the screen
    P = o + lam[..., None] * d
    phi = np.arctan2(P[..., 0], -(zc - P[..., 2]))
    return CYL_R * phi, P[..., 1], ok & (lam > 0)


_BG = None


def background():
    global _BG
    if _BG is None:
        rng = np.random.default_rng(7)
        low = rng.random((9, 16, 3)).astype(np.float32)
        tex = cv2.resize(low, (W_OUT, H_OUT), interpolation=cv2.INTER_CUBIC)
        fine = cv2.resize(rng.random((90, 160)).astype(np.float32), (W_OUT, H_OUT), interpolation=cv2.INTER_CUBIC)
        base = np.array([150, 120, 95], np.float32)     # warm desk / wall, never green-dominant
        img = base * (0.75 + 0.35 * tex.mean(axis=2, keepdims=True)) + 14 * (fine[..., None] - 0.5)
        img[..., 1] = np.minimum(img[..., 1], 0.5 * (img[..., 0] + img[..., 2]) + 4)
        _BG = np.clip(img, 0, 255)
    return _BG


def render_device(surface, R, t):
    s, yv, ok = raycast(surface, R, t, SS)
    u = s / PLATE_W + 0.5
    v = yv / PLATE_H + 0.5
    plate = ok & (u >= 0) & (u <= 1) & (v >= 0) & (v <= 1)
    device = ok & (np.abs(s) <= PLATE_W / 2 + BEZEL) & (np.abs(yv) <= PLATE_H / 2 + BEZEL)
    img = np.zeros(s.shape + (3,), np.float32)
    alpha = device.astype(np.float32)
    img[device] = (20, 20, 23)
    light = (1.0 - 0.22 * np.clip(u, 0, 1) - 0.10 * np.clip(v, 0, 1))[..., None]      # lighting gradient
    green = np.array([28, 222, 40], np.float32) * light
    img[plate] = green[plate]
    img = cv2.resize(img, (W_OUT, H_OUT), interpolation=cv2.INTER_AREA)
    alpha = cv2.resize(alpha, (W_OUT, H_OUT), interpolation=cv2.INTER_AREA)
    return img, alpha


def occluder_shapes(occlusion, corners, i):
    """Capsules [(p0, p1, radius)], and a colour, for frame i. corners = TL, TR, BL, BR in px."""
    tau = i / (FRAMES - 1)
    TL, TR, BL, BR = (np.array(c, float) for c in corners)
    cen = (TL + TR + BL + BR) / 4
    short = min(np.linalg.norm(TL - BL), np.linalg.norm(TR - BR), np.linalg.norm(TL - TR), np.linalg.norm(BL - BR))
    a = smooth((tau - 0.18) / 0.17) if tau < 0.5 else smooth((0.82 - tau) / 0.17)
    skin, fur, dark = (206, 160, 134), (124, 88, 54), (34, 30, 28)

    def unit(vec):
        return vec / max(1e-9, np.linalg.norm(vec))

    if occlusion == "clean":
        return [], skin
    if occlusion in ("cornertouch", "cornercover"):
        r = (0.09 if occlusion == "cornertouch" else 0.30) * short
        o = unit(BR - cen)
        c = BR + o * (1 - a) * 4.5 * r
        return [(c, c, r), (c, c + o * 3000, 0.75 * r)], skin
    if occlusion == "edgecover":
        r = 0.20 * short
        o = unit((BL + BR) / 2 - cen)
        off = o * ((1 - a) * 3.6 * r + 0.3 * r)
        return [(BL + (BL - BR) * 0.3 + off, BR + (BR - BL) * 0.3 + off, r)], fur
    if occlusion == "twocorners":
        r = 0.16 * short
        o = unit((TR + BR) / 2 - cen)
        off = o * (1 - a) * 3.6 * r
        return [(TR + (TR - BR) * 0.15 + off, BR + (BR - TR) * 0.15 + off, r)], dark
    if occlusion == "split":
        uu = 1.25 - 0.70 * min(1.0, max(0.0, (tau - 0.12) / 0.76))
        top = TL + (TR - TL) * uu
        bot = BL + (BR - BL) * (uu + 0.06)
        r = 0.045 * np.linalg.norm(TR - TL)
        return [(top + (top - bot) * 0.35, bot + (bot - top) * 3.0, r)], dark
    raise ValueError(occlusion)


def occluder_alpha(occlusion, corners, i):
    shapes, colour = occluder_shapes(occlusion, corners, i)
    m = np.zeros((H_OUT * SS, W_OUT * SS), np.uint8)
    for p0, p1, r in shapes:
        a = (int(round(p0[0] * SS * 16)), int(round(p0[1] * SS * 16)))
        b = (int(round(p1[0] * SS * 16)), int(round(p1[1] * SS * 16)))
        lim = 1 << 28
        a = tuple(int(np.clip(x, -lim, lim)) for x in a)
        b = tuple(int(np.clip(x, -lim, lim)) for x in b)
        cv2.line(m, a, b, 255, max(1, int(round(2 * r * SS))), cv2.LINE_AA, shift=4)
        cv2.circle(m, a, int(round(r * SS * 16)), 255, -1, cv2.LINE_AA, shift=4)
        cv2.circle(m, b, int(round(r * SS * 16)), 255, -1, cv2.LINE_AA, shift=4)
    if not shapes:
        return np.zeros((H_OUT, W_OUT), np.float32), colour
    m = cv2.GaussianBlur(m, (0, 0), 1.6 * SS)
    return cv2.resize(m, (W_OUT, H_OUT), interpolation=cv2.INTER_AREA).astype(np.float32) / 255, colour


_SHADE = None


def shade():
    global _SHADE
    if _SHADE is None:
        rng = np.random.default_rng(11)
        _SHADE = 0.82 + 0.36 * cv2.resize(rng.random((36, 64)).astype(np.float32), (W_OUT, H_OUT), interpolation=cv2.INTER_CUBIC)
    return _SHADE


def ground_truth(name, i):
    motion, surface, occlusion = name.split("-")
    R, t = pose(motion, surface, i)
    corners = [project(surface, R, t, u, v) for u, v in ((0, 0), (1, 0), (0, 1), (1, 1))]
    return motion, surface, occlusion, R, t, corners


def render_frame(name, i, noise_rng):
    motion, surface, occlusion, R, t, corners = ground_truth(name, i)
    dev, dalpha = render_device(surface, R, t)
    img = background() * (1 - dalpha[..., None]) + dev * dalpha[..., None]
    oa, colour = occluder_alpha(occlusion, corners, i)
    occ = np.array(colour, np.float32) * shade()[..., None]
    img = img * (1 - oa[..., None]) + occ * oa[..., None]
    img = cv2.GaussianBlur(img, (0, 0), 0.55)
    img += noise_rng.normal(0, 1.5, img.shape).astype(np.float32)
    return np.clip(img, 0, 255).astype(np.uint8), corners, oa


def frame_truth(name, i, with_maps=False):
    """Ground truth of frame i: corners, which corners/edges the occluder covers, and (optionally)
    the per-pixel plate (u, v) map and occluder alpha at output resolution."""
    motion, surface, occlusion, R, t, corners = ground_truth(name, i)
    oa, _ = occluder_alpha(occlusion, corners, i)

    def alpha_at(p):
        x, y = int(math.floor(p[0])), int(math.floor(p[1]))
        return float(oa[y, x]) if 0 <= x < W_OUT and 0 <= y < H_OUT else 0.0

    edges = {"top": ((0, 0), (1, 0)), "right": ((1, 0), (1, 1)), "bottom": ((0, 1), (1, 1)), "left": ((0, 0), (0, 1))}
    cover = {}
    for k, ((u0, v0), (u1, v1)) in edges.items():
        hits = [alpha_at(project(surface, R, t, u0 + (u1 - u0) * q / 40, v0 + (v1 - v0) * q / 40)) > 0.5 for q in range(41)]
        cover[k] = sum(hits) / 41
    out = {"corners": [[round(c[0], 3), round(c[1], 3)] for c in corners],
           "cornerCovered": [alpha_at(c) > 0.5 for c in corners], "edgeCovered": {k: round(v, 3) for k, v in cover.items()}}
    if with_maps:
        s, yv, ok = raycast(surface, R, t, 1)
        u, v = s / PLATE_W + 0.5, yv / PLATE_H + 0.5
        out["u"], out["v"] = u, v
        out["plate"] = ok & (u >= 0) & (u <= 1) & (v >= 0) & (v <= 1)
        out["occ"] = oa
    return out


def render_clip(name, out_dir):
    path = os.path.join(out_dir, name + ".mp4")
    enc = subprocess.Popen(
        ["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W_OUT}x{H_OUT}", "-r", str(FPS), "-i", "pipe:0",
         "-c:v", "libx264", "-preset", "medium", "-crf", "17", "-pix_fmt", "yuv420p", path], stdin=subprocess.PIPE)
    rng = np.random.default_rng(zlib.crc32(name.encode()) % (2 ** 31))
    truth = []
    for i in range(FRAMES):
        img, _, _ = render_frame(name, i, rng)
        enc.stdin.write(img.tobytes())
        truth.append(frame_truth(name, i))
    enc.stdin.close()
    enc.wait()
    with open(os.path.join(out_dir, name + ".json"), "w") as f:
        json.dump({"name": name, "width": W_OUT, "height": H_OUT, "fps": FPS, "frames": truth}, f)
    return path


if __name__ == "__main__":
    out = sys.argv[1]
    names = sys.argv[2:] or scene_names()
    os.makedirs(out, exist_ok=True)
    for n in names:
        render_clip(n, out)
        print("rendered", n, flush=True)
