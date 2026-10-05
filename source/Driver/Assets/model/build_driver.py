"""Driver character for the Driver plugin (rogue.driver): original, fully scripted, reproducible.

    blender -b --factory-startup --python build_driver.py -- [--out DIR] [--no-fbx]

Builds (no external meshes, no sample content):
  * stylised racing driver: one-piece suit with neon stripes/piping, gloves, boots, padded collar, full-face helmet
  * UE-compatible skeleton (55 bones: root, pelvis, spine_01..03, neck_01, head, clavicle/upperarm/lowerarm/
    lowerarm_twist_01/hand + 15 finger bones per side, thigh/calf/foot/ball per side), A-pose rest
  * LOD0 (real fingers) and LOD1 (mitten hands, fewer loops); helmet + visor rigid on `head`
  * palette atlases (PNG) generated here
  * poses: rest A-pose, seated driving pose (solved with the same fit/IK rules the plugin will use), breathing layer,
    the sport-bike riding pose preset ride_sportbike (anim_clips.ride_clip, on the Bikes plugin's S1000RR sockets)
Writes into DIR (default: this folder):
  driver.drm, driver_anims.dra          runtime files (see drm_io.py for the format)
  driver.fbx                            for Unreal Engine (one skeletal mesh, actions A_Pose, Seated_Drive, Seated_Breathe)
  driver_atlas.png, driver_emit.png, driver_helmet.png, driver_helmet_emit.png
  driver_sockets.json                   sockets (not bones) for UE / tools
  driver.blend                          working file (git-ignored)

Blender convention: character faces -Y, Z up, metres, character's left = +X. Unity conversion (char -Y fwd -> Unity +Z):
C = [[-1,0,0],[0,0,1],[0,-1,0]], p_u = C p, M_u = C M Cᵀ. det C = -1, so triangle order is flipped on export.
"""
import bpy, bmesh, math, os, sys, json, hashlib
from math import sin, cos, pi, radians, copysign
from mathutils import Vector, Matrix, Quaternion
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import drm_io
import anim_clips

HS = 1.1          # hand scale (toon read)
C3 = Matrix(((-1, 0, 0), (0, 0, 1), (0, -1, 0)))


def nv(*a):
    return Vector(a[0] if len(a) == 1 else a).normalized()


# ================================================================================================== skeleton
def skeleton_def():
    """-> (bones dict name -> dict(head, tail, parent, roll), info dict). Parents always defined before children."""
    B = {}; info = {}

    def add(n, h, t, p, roll=(0, -1, 0)):
        B[n] = dict(head=Vector(h), tail=Vector(t), parent=p, roll=Vector(roll))

    add("root", (0, 0, 0), (0, 0.12, 0), None, (0, 0, 1))
    P, S1, S2, S3 = Vector((0, 0, 0.97)), Vector((0, 0.0, 1.03)), Vector((0, 0.004, 1.13)), Vector((0, 0.008, 1.25))
    N, H, HT = Vector((0, 0.012, 1.455)), Vector((0, 0.0, 1.555)), Vector((0, 0.0, 1.72))
    add("pelvis", P, S1, "root"); add("spine_01", S1, S2, "pelvis"); add("spine_02", S2, S3, "spine_01")
    add("spine_03", S3, N, "spine_02"); add("neck_01", N, H, "spine_03"); add("head", H, HT, "neck_01")
    for sg, sf in ((1, "_l"), (-1, "_r")):
        def m(v, sg=sg): return Vector((sg * v[0], v[1], v[2]))
        CL0, SH = m((0.035, -0.005, 1.405)), m((0.20, 0.02, 1.425))
        add("clavicle" + sf, CL0, SH, "spine_03")
        ua = m(nv(0.70, -0.06, -0.71)); EL = SH + ua * 0.29
        la = m(nv(0.62, -0.25, -0.74)); WR = EL + la * 0.26
        add("upperarm" + sf, SH, EL, "clavicle" + sf)
        add("lowerarm" + sf, EL, WR, "upperarm" + sf)
        add("lowerarm_twist_01" + sf, EL + la * 0.17, EL + la * 0.24, "lowerarm" + sf)
        a = la.copy()
        n0 = m((-1, 0, 0)); n = (n0 - a * a.dot(n0)).normalized()          # palm normal (faces the thigh)
        t = a.cross(n).normalized()
        if t.y > 0: t = -t                                                   # thumb side = forward
        add("hand" + sf, WR, WR + a * 0.085 * HS, "lowerarm" + sf, n)
        fing = {}
        for fn, off, base, lens, spread, rad in (("index", 0.033, 0.085, (0.040, 0.025, 0.020), 0.06, 0.0132),
                                                 ("middle", 0.011, 0.087, (0.044, 0.028, 0.021), 0.0, 0.0136),
                                                 ("ring", -0.011, 0.084, (0.041, 0.026, 0.020), -0.05, 0.0130),
                                                 ("pinky", -0.032, 0.078, (0.032, 0.020, 0.018), -0.11, 0.0118)):
            p0 = WR + a * base * HS + t * off * HS
            d = (a + t * spread).normalized(); pts = [p0]; curl = (12, 16, 10)
            for k in range(3):
                kx = d.cross(n).normalized(); d = (Matrix.Rotation(radians(curl[k]), 3, kx) @ d).normalized()
                pts.append(pts[-1] + d * lens[k] * HS)
            for k in range(3):
                add("%s_%02d%s" % (fn, k + 1, sf), pts[k], pts[k + 1],
                    "hand" + sf if k == 0 else "%s_%02d%s" % (fn, k, sf), n)
            fing[fn] = (pts, rad * HS)
        p0 = WR + a * 0.028 * HS + t * 0.026 * HS + n * 0.014 * HS
        d = (a * 0.75 + t * 0.45 + n * 0.40).normalized(); pts = [p0]
        for k, (ln, cu) in enumerate(zip((0.040, 0.030, 0.026), (0, 12, 12))):
            kx = d.cross(n).normalized(); d = (Matrix.Rotation(radians(cu), 3, kx) @ d).normalized()
            pts.append(pts[-1] + d * ln * HS)
        for k in range(3):
            add("thumb_%02d%s" % (k + 1, sf), pts[k], pts[k + 1], "hand" + sf if k == 0 else "thumb_%02d%s" % (k, sf), n)
        fing["thumb"] = (pts, 0.0135 * HS)
        HIP, KN, AN = m((0.09, 0.0, 0.935)), m((0.095, -0.012, 0.495)), m((0.098, 0.012, 0.075))
        BALL, TOE = m((0.100, -0.135, 0.022)), m((0.100, -0.195, 0.022))
        add("thigh" + sf, HIP, KN, "pelvis"); add("calf" + sf, KN, AN, "thigh" + sf)
        add("foot" + sf, AN, BALL, "calf" + sf, (0, 0, 1)); add("ball" + sf, BALL, TOE, "foot" + sf, (0, 0, 1))
        info[sf] = dict(SH=SH, EL=EL, WR=WR, a=a, n=n, t=t, ua=ua, la=la, fing=fing, HIP=HIP, KN=KN, AN=AN, BALL=BALL,
                        TOE=TOE, CL0=CL0, sg=sg)
    order = ["root", "pelvis", "spine_01", "spine_02", "spine_03", "neck_01", "head"]
    for sf in ("_l", "_r"):
        order += [b + sf for b in ("clavicle", "upperarm", "lowerarm", "lowerarm_twist_01", "hand")]
        for fn in ("thumb", "index", "middle", "ring", "pinky"):
            order += ["%s_%02d%s" % (fn, k, sf) for k in (1, 2, 3)]
    for sf in ("_l", "_r"):
        order += [b + sf for b in ("thigh", "calf", "foot", "ball")]
    assert len(order) == len(B) == 55, (len(order), len(B))
    info["order"] = order
    return B, info


def sockets_def(info):
    """-> [(name, bone, pos (blender world, rest), z_dir, y_dir)]; frames are given as Unity-space axes later."""
    S = [("eye_c", "head", Vector((0, -0.085, 1.615)), Vector((0, -1, 0)), Vector((0, 0, 1))),
         ("hip_c", "pelvis", Vector((0, 0.02, 0.86)), Vector((0, -1, 0)), Vector((0, 0, 1))),
         ("helmet", "head", Vector((0, 0.004, 1.628)), Vector((0, -1, 0)), Vector((0, 0, 1)))]
    for sf in ("_l", "_r"):
        I = info[sf]
        S.append(("grip" + sf, "hand" + sf, I["WR"] + I["a"] * 0.080 + I["n"] * 0.036, I["t"], I["n"]))
        S.append(("heel" + sf, "foot" + sf, Vector((I["AN"].x, 0.072, 0.0)), Vector((0, -1, 0)), Vector((0, 0, 1))))
        S.append(("toe" + sf, "ball" + sf, Vector((I["BALL"].x, -0.214, 0.0)), Vector((0, -1, 0)), Vector((0, 0, 1))))
    return S


# ================================================================================================== atlases
SW = {"suit": 0, "panel": 1, "pink": 2, "cyan": 3, "glove": 4, "boot": 5, "sole": 6, "belt": 7, "buckle": 8,
      "collar": 9, "neck": 10, "strap": 11, "knuckle": 12, "suit_dk": 13}
SWC = {"suit": "#15122B", "panel": "#2B2257", "pink": "#FF2E88", "cyan": "#18D8FF", "glove": "#121218",
       "boot": "#ECECF3", "sole": "#1C1B22", "belt": "#24213A", "buckle": "#C3C8D8", "collar": "#1F1A3D",
       "neck": "#16141E", "strap": "#2A2840", "knuckle": "#18D8FF", "suit_dk": "#0F0D20"}
EMIT_SW = {"pink": 1.0, "cyan": 1.0, "knuckle": 0.7}
EMBLEM = 100                                  # planar-mapped chest emblem region (atlas px 384..512 both axes)
HSW = {"shell": 0, "pad": 1, "fin": 2, "hcyan": 3, "visor": 4, "vedge": 5, "hwhite": 6}
HSWC = {"shell": "#0C0C12", "pad": "#2A2A33", "fin": "#FF2E88", "hcyan": "#18D8FF", "visor": "#0B1020",
        "vedge": "#18D8FF", "hwhite": "#E8E8F0"}
HGRAD = 200                                   # helmet gradient stripe region


def hexc(h): return np.array([int(h[i:i + 2], 16) / 255.0 for i in (1, 3, 5)])


def swatch_box(sid, size=512, cells=8):
    c, r = sid % cells, sid // cells; px = size // cells; m = 10
    return ((c * px + m) / size, (r * px + m) / size, (px - 2 * m) / size)


def suit_uv(sid, u, v):
    """sid swatch, (u, v) in 0..1 inside the swatch's inner box."""
    if sid == EMBLEM:
        return (384 + 6 + u * 116) / 512.0, (384 + 6 + v * 116) / 512.0
    x, y, s = swatch_box(sid)
    return x + u * s, y + v * s


def helmet_uv(sid, u, v):
    if sid == HGRAD:
        return (8 + u * 240) / 256.0, (150 + v * 70) / 256.0
    x, y, s = swatch_box(sid, 256, 4)
    return x + u * s, y + v * s


def chevron_mask(U, V, y0):
    """two-stroke chevron (V) of half-thickness 0.06 with its tip at (0.5, y0)."""
    out = np.zeros_like(U)
    for sx in (-1, 1):
        ax, ay, bx, by = 0.5, y0, 0.5 + sx * 0.26, y0 + 0.30
        dx, dy = bx - ax, by - ay
        t = np.clip(((U - ax) * dx + (V - ay) * dy) / (dx * dx + dy * dy), 0, 1)
        d = np.hypot(U - (ax + t * dx), V - (ay + t * dy))
        out = np.maximum(out, np.clip((0.055 - d) / 0.012, 0, 1))
    return out


def make_atlases(outdir):
    S = 512
    col = np.zeros((S, S, 3)); emi = np.zeros((S, S, 3))
    col[:] = hexc("#15122B")
    for name, sid in SW.items():
        c, r = sid % 8, sid // 8
        col[r * 64:(r + 1) * 64, c * 64:(c + 1) * 64] = hexc(SWC[name])
        if name in EMIT_SW: emi[r * 64:(r + 1) * 64, c * 64:(c + 1) * 64] = hexc(SWC[name]) * EMIT_SW[name]
    # chest emblem region: suit background, pink chevron over a cyan one
    yy, xx = np.mgrid[0:128, 0:128]; U = (xx - 6 + 0.5) / 116.0; V = (yy - 6 + 0.5) / 116.0
    reg = np.zeros((128, 128, 3)); reg[:] = hexc("#15122B"); ereg = np.zeros((128, 128, 3))
    for y0, cname in ((0.22, "#18D8FF"), (0.40, "#FF2E88")):
        mk = chevron_mask(U, V, y0)[..., None]
        reg = reg * (1 - mk) + hexc(cname) * mk; ereg = ereg * (1 - mk) + hexc(cname) * mk
    col[384:512, 384:512] = reg; emi[384:512, 384:512] = ereg
    # helmet
    HSz = 256
    hcol = np.zeros((HSz, HSz, 3)); hemi = np.zeros((HSz, HSz, 3)); hcol[:] = hexc("#0C0C12")
    for name, sid in HSW.items():
        c, r = sid % 4, sid // 4
        hcol[r * 64:(r + 1) * 64, c * 64:(c + 1) * 64] = hexc(HSWC[name])
    for name, k in (("vedge", 1.0), ("fin", 0.35), ("hcyan", 0.8)):
        sid = HSW[name]; c, r = sid % 4, sid // 4
        hemi[r * 64:(r + 1) * 64, c * 64:(c + 1) * 64] = hexc(HSWC[name]) * k
    g = np.linspace(0, 1, 240)[None, :, None]
    grad = hexc("#FF2E88") * (1 - g) + hexc("#18D8FF") * g
    hcol[146:224, 8:248] = grad; hemi[146:224, 8:248] = grad * 0.45
    paths = {}
    for fname, arr in (("driver_atlas.png", col), ("driver_emit.png", emi), ("driver_helmet.png", hcol),
                       ("driver_helmet_emit.png", hemi)):
        h, w = arr.shape[:2]
        img = bpy.data.images.get(fname) or bpy.data.images.new(fname, w, h, alpha=False)
        rgba = np.concatenate([arr, np.ones((h, w, 1))], axis=2).astype(np.float32)
        img.pixels.foreach_set(rgba.ravel())
        p = os.path.join(outdir, fname); img.filepath_raw = p; img.file_format = "PNG"; img.save()
        paths[fname] = img
    return paths


# ================================================================================================== mesh builder
class MB:
    """Collects vertices (+ weights, flags, part id) and faces (+ per-corner UVs, material slot)."""
    def __init__(s, uvf=suit_uv):
        s.co, s.w, s.flag, s.part, s.faces = [], [], [], [], []; s.uvf = uvf; s.parts = {}

    def vert(s, p, w, flag=0, part=0):
        s.co.append(Vector(p)); s.w.append(dict(w)); s.flag.append(flag); s.part.append(part); return len(s.co) - 1

    def face(s, vids, uvs, slot=0):
        s.faces.append((list(vids), list(uvs), slot))

    def begin(s, name):
        s._cur = (name, len(s.co), len(s.faces))

    def end(s):
        name, v0, f0 = s._cur; s.parts[name] = (v0, len(s.co), f0, len(s.faces))

    def mirror(s, src, dst, part_id):
        """copies part src mirrored in X (_l -> _r weights), restoring outward winding."""
        v0, v1, f0, f1 = s.parts[src]; base = len(s.co); s.begin(dst)
        for i in range(v0, v1):
            c = s.co[i]
            s.vert((-c.x, c.y, c.z), {k.replace("_l", "_r") if k.endswith("_l") else k: x for k, x in s.w[i].items()},
                   s.flag[i], part_id)
        for k in range(f0, f1):
            vids, uvs, slot = s.faces[k]
            s.face([base + (v - v0) for v in reversed(vids)], list(reversed(uvs)), slot)
        s.end()


def lerpw(a, b, t):
    d = {}
    for k, v in a.items(): d[k] = d.get(k, 0) + v * (1 - t)
    for k, v in b.items(): d[k] = d.get(k, 0) + v * t
    return {k: v for k, v in d.items() if v > 1e-4}


def chain(sv, ctrl):
    if sv <= ctrl[0][0]: return dict(ctrl[0][1])
    for (s0, w0), (s1, w1) in zip(ctrl, ctrl[1:]):
        if sv <= s1:
            t = (sv - s0) / max(1e-9, s1 - s0); t = t * t * (3 - 2 * t); return lerpw(w0, w1, t)
    return dict(ctrl[-1][1])


def ring(c, axis, ref, n, rpx, rnx=None, rpy=None, rny=None, e=2.0, clampz=None):
    """ring of n points around axis; column j is centred at angle j*2pi/n from ref (CCW about axis)."""
    z = axis.normalized(); x = (ref - z * ref.dot(z)).normalized(); y = z.cross(x)
    rnx = rpx if rnx is None else rnx; rpy = rpx if rpy is None else rpy; rny = rpy if rny is None else rny
    pts = []
    for j in range(n):
        th = 2 * pi * (j - 0.5) / n; c_, s_ = cos(th), sin(th); ex = 2.0 / e
        cx = copysign(abs(c_) ** ex, c_); sy = copysign(abs(s_) ** ex, s_)
        p = c + x * ((rpx if c_ >= 0 else rnx) * cx) + y * ((rpy if s_ >= 0 else rny) * sy)
        if clampz is not None and p.z < clampz: p.z = clampz
        pts.append(p)
    return pts


def loft(mb, rings, sw, part=0, loop=False, cap0=None, cap1=None):
    """rings: list of (pts, weights, flag). sw(i, j) -> swatch id (band i between ring i and i+1, column j).
    cap0/cap1: None or (tip point or None, swatch) fan caps at the first/last ring."""
    nR = len(rings); n = len(rings[0][0])
    ids = [[mb.vert(p, w, fl, part) for p in pts] for pts, w, fl in rings]
    bands = nR if loop else nR - 1
    for i in range(bands):
        i2 = (i + 1) % nR
        for j in range(n):
            j2 = (j + 1) % n; sid = sw(i, j)
            vids = [ids[i][j], ids[i][j2], ids[i2][j2], ids[i2][j]]
            if sid == EMBLEM:
                uvs = [mb.uvf(EMBLEM, (mb.co[v].x + 0.11) / 0.22, (mb.co[v].z - 1.15) / 0.22) for v in vids]
            else:
                den = max(1, bands)
                uvs = [mb.uvf(sid, jj / n, ii / den) for jj, ii in ((j, i), (j + 1, i), (j + 1, i + 1), (j, i + 1))]
            mb.face(vids, uvs)
    for cap, ri, rev in ((cap0, 0, True), (cap1, nR - 1, False)):
        if cap is None: continue
        tip, sid = cap
        pts, w, fl = rings[ri]
        cpos = tip if tip is not None else sum(pts, Vector()) / n
        c = mb.vert(cpos, w, fl, part); cu = mb.uvf(sid, 0.5, 0.5)
        for j in range(n):
            j2 = (j + 1) % n
            a1 = 2 * pi * j / n; a2 = 2 * pi * (j + 1) / n
            u1 = mb.uvf(sid, 0.5 + 0.45 * cos(a1), 0.5 + 0.45 * sin(a1)); u2 = mb.uvf(sid, 0.5 + 0.45 * cos(a2), 0.5 + 0.45 * sin(a2))
            if rev: mb.face([ids[ri][j2], ids[ri][j], c], [u2, u1, cu])
            else: mb.face([ids[ri][j], ids[ri][j2], c], [u1, u2, cu])
    return ids


def polyline_at(pts, sv):
    """point + tangent at arc length sv along polyline pts (extrapolates); tangent blended within 3 cm of joints."""
    segs = [(pts[k], pts[k + 1]) for k in range(len(pts) - 1)]
    lens = [(b - a).length for a, b in segs]; acc = 0.0
    for k, ((a, b), L) in enumerate(zip(segs, lens)):
        if sv <= acc + L or k == len(segs) - 1:
            d = (b - a).normalized(); p = a + d * (sv - acc); tan = d.copy()
            if k + 1 < len(segs) and acc + L - sv < 0.03:
                dn = (segs[k + 1][1] - segs[k + 1][0]).normalized(); f = 0.5 * (1 - (acc + L - sv) / 0.03); tan = (d * (1 - f) + dn * f).normalized()
            if k > 0 and sv - acc < 0.03:
                dp = (segs[k - 1][1] - segs[k - 1][0]).normalized(); f = 0.5 * (1 - (sv - acc) / 0.03); tan = (d * (1 - f) + dp * f).normalized()
            return p, tan
        acc += L


PART = {"torso": 1, "collar": 2, "deltoid_l": 3, "arm_l": 4, "cuff_l": 5, "hand_l": 6, "leg_l": 7, "boot_l": 8,
        "deltoid_r": 13, "arm_r": 14, "cuff_r": 15, "hand_r": 16, "leg_r": 17, "boot_r": 18}


def build_body(B, info, lod):
    L0 = lod == 0
    mb = MB(suit_uv)
    NT = 24 if L0 else 14; NL = 16 if L0 else 10; NB = 16 if L0 else 10; NF = 6
    # ---------------------------------------------------------------- torso + neck
    TOR = [  # z, side, back, front, cy, e, lod1
        (0.855, 0.138, 0.092, 0.080, 0.012, 2.4, 1), (0.900, 0.168, 0.112, 0.098, 0.012, 2.4, 1),
        (0.950, 0.176, 0.118, 0.102, 0.010, 2.4, 0), (0.995, 0.164, 0.106, 0.098, 0.006, 2.4, 1),
        (1.035, 0.152, 0.098, 0.094, 0.004, 2.4, 1), (1.090, 0.149, 0.096, 0.098, 0.002, 2.4, 0),
        (1.160, 0.158, 0.100, 0.112, 0.002, 2.5, 1), (1.230, 0.172, 0.104, 0.122, 0.004, 2.6, 0),
        (1.300, 0.181, 0.106, 0.118, 0.008, 2.6, 1), (1.352, 0.184, 0.104, 0.107, 0.010, 2.6, 1),
        (1.366, 0.183, 0.102, 0.103, 0.010, 2.6, 1), (1.405, 0.166, 0.092, 0.086, 0.012, 2.4, 1),
        (1.440, 0.110, 0.075, 0.068, 0.012, 2.2, 0), (1.466, 0.066, 0.060, 0.058, 0.012, 2.0, 1),
        (1.505, 0.056, 0.055, 0.053, 0.010, 2.0, 1), (1.560, 0.052, 0.052, 0.050, 0.006, 2.0, 0),
        (1.600, 0.048, 0.048, 0.046, 0.004, 2.0, 1)]
    TOR = [r for r in TOR if L0 or r[6]]
    spine = [(0.965, {"pelvis": 1}), (1.05, {"spine_01": 1}), (1.135, {"spine_02": 1}), (1.245, {"spine_03": 1}),
             (1.44, {"spine_03": 1}), (1.50, {"neck_01": 1}), (1.55, {"neck_01": 1}), (1.585, {"head": 1})]
    zs = [r[0] for r in TOR]
    mb.begin("torso")
    ids = []
    rings = []
    for z, rx, rb, rf, cy, e, _ in TOR:
        pts = ring(Vector((0, cy, z)), Vector((0, 0, 1)), Vector((1, 0, 0)), NT, rx, rx, rb, rf, e)
        wl = []
        for p in pts:
            w = chain(z, spine)
            ax = abs(p.x); sf = "_l" if p.x > 0 else "_r"
            ft = min(1, max(0, (0.955 - z) / 0.10)) * min(1, max(0, (ax - 0.02) / 0.12)) * min(1, max(0, (0.06 - p.y) / 0.10))
            if ft > 0: w = lerpw(w, {"thigh" + sf: 1}, 0.5 * ft)
            fc = min(1, max(0, (z - 1.33) / 0.08)) * min(1, max(0, (ax - 0.07) / 0.08))
            if fc > 0 and z < 1.47: w = lerpw(w, {"clavicle" + sf: 1}, 0.55 * fc)
            wl.append(w)
        rings.append((pts, wl, 1 if z >= 1.50 else 0))
    # per-vertex weights: loft takes one dict per ring -> add vertices manually
    ids = [[mb.vert(p, w, fl, PART["torso"]) for p, w in zip(pts, wl)] for pts, wl, fl in rings]
    nR = len(rings); jF = 3 * NT // 4

    def tsw(i, j, zc, xc, yc):
        z0, z1 = zs[i], zs[i + 1]
        if z0 >= 1.466: return SW["neck"]
        if abs(z0 - 0.995) < 1e-6: return SW["buckle"] if j == jF else SW["belt"]
        if abs(z0 - 1.352) < 1e-6: return SW["pink"]
        if z0 >= 1.366: return SW["panel"]
        if (yc < -0.05 or yc > 0.06) and abs(xc) < 0.07 and 1.16 <= zc <= 1.352: return EMBLEM   # chest + back
        if j == 0 or j == NT // 2: return SW["pink"]
        if j == 1 or j == NT // 2 - 1: return SW["cyan"]
        return SW["suit"]
    for i in range(nR - 1):
        for j in range(NT):
            j2 = (j + 1) % NT
            vids = [ids[i][j], ids[i][j2], ids[i + 1][j2], ids[i + 1][j]]
            cen = sum((mb.co[v] for v in vids), Vector()) / 4
            sid = tsw(i, j, cen.z, cen.x, cen.y)
            if sid == EMBLEM:
                uvs = [suit_uv(EMBLEM, (mb.co[v].x + 0.11) / 0.22, (mb.co[v].z - 1.15) / 0.22) for v in vids]
            else:
                uvs = [suit_uv(sid, jj / NT, ii / (nR - 1)) for jj, ii in ((j, i), (j + 1, i), (j + 1, i + 1), (j, i + 1))]
            mb.face(vids, uvs)
    # bottom cap (crotch) and top cap (inside helmet)
    for ri, rev, sid in ((0, True, SW["suit_dk"]), (nR - 1, False, SW["neck"])):
        c = mb.vert(sum((mb.co[v] for v in ids[ri]), Vector()) / NT + Vector((0, 0, -0.012 if rev else 0.01)),
                    mb.w[ids[ri][0]], rings[ri][2], PART["torso"])
        cu = suit_uv(sid, 0.5, 0.5)
        for j in range(NT):
            j2 = (j + 1) % NT
            u1 = suit_uv(sid, 0.5 + 0.45 * cos(2 * pi * j / NT), 0.5 + 0.45 * sin(2 * pi * j / NT))
            u2 = suit_uv(sid, 0.5 + 0.45 * cos(2 * pi * j2 / NT), 0.5 + 0.45 * sin(2 * pi * j2 / NT))
            if rev: mb.face([ids[ri][j2], ids[ri][j], c], [u2, u1, cu])
            else: mb.face([ids[ri][j], ids[ri][j2], c], [u1, u2, cu])
    # neck plug: closes the torso at the neck base for the head-less (driver view) variant
    kN = zs.index(1.466)
    c = mb.vert(sum((mb.co[v] for v in ids[kN]), Vector()) / NT + Vector((0, 0, -0.004)), {"spine_03": 0.5, "neck_01": 0.5}, 0, PART["torso"])
    plug = [mb.vert(mb.co[v] + (mb.co[c] - mb.co[v]) * 0.04, mb.w[v], 0, PART["torso"]) for v in ids[kN]]
    for j in range(NT):
        j2 = (j + 1) % NT; cu = suit_uv(SW["suit_dk"], 0.5, 0.5)
        mb.face([plug[j], plug[j2], c], [cu, suit_uv(SW["suit_dk"], 0.9, 0.5), suit_uv(SW["suit_dk"], 0.5, 0.9)])
    mb.end()
    # ---------------------------------------------------------------- collar (padded neck brace)
    mb.begin("collar")
    NCp = 20 if L0 else 12; NCs = 8 if L0 else 6
    rings = []
    for k in range(NCp):
        ph = 2 * pi * k / NCp
        rad = Vector((cos(ph), sin(ph), 0))
        cen = Vector((0.083 * cos(ph), 0.012 + 0.077 * sin(ph), 1.468 + 0.012 * max(0, sin(ph)) ** 1.5))
        tan = Vector((-0.083 * sin(ph), 0.077 * cos(ph), 0)).normalized()
        pts = ring(cen, tan, rad, NCs, 0.024, 0.018, 0.030, 0.030)
        rings.append((pts, {"spine_03": 0.45, "neck_01": 0.55}, 1))
    jTop = 3 * NCs // 4
    loft(mb, rings, lambda i, j: SW["pink"] if j == jTop else SW["collar"], PART["collar"], loop=True)
    mb.end()
    # ---------------------------------------------------------------- left side (mirrored later)
    I = info["_l"]; sf = "_l"
    # deltoid / shoulder yoke cap
    mb.begin("deltoid_l")
    ND = 12 if L0 else 8
    cen0 = I["SH"] + Vector((0, 0, 0.006)); ax = I["ua"]
    ss = [-0.068, -0.05, -0.025, 0.0, 0.025, 0.05, 0.068] if L0 else [-0.065, -0.03, 0.0, 0.03, 0.065]
    rings = []
    for s_ in ss:
        r = 0.066 * math.sqrt(max(0.02, 1 - (s_ / 0.078) ** 2))
        pts = ring(cen0 + ax * s_, ax, Vector((0, 0, 1)), ND, r, r, r * 0.95, r * 0.95)
        rings.append((pts, chain(s_, [(-0.06, {"clavicle_l": 0.7, "upperarm_l": 0.3}), (0.0, {"clavicle_l": 0.3, "upperarm_l": 0.7}),
                                      (0.06, {"upperarm_l": 1})]), 0))
    loft(mb, rings, lambda i, j: SW["pink"] if i == len(ss) - 2 else SW["panel"], PART["deltoid_l"],
         cap0=(cen0 + ax * -0.078, SW["panel"]), cap1=(cen0 + ax * 0.078, SW["pink"]))
    mb.end()
    # arm
    mb.begin("arm_l")
    path = [I["SH"], I["EL"], I["WR"]]
    ARM = [(-0.045, 0.050, 1), (0.0, 0.056, 1), (0.06, 0.054, 0), (0.13, 0.050, 1), (0.20, 0.047, 0), (0.255, 0.044, 1),
           (0.29, 0.043, 1), (0.325, 0.043, 1), (0.38, 0.045, 0), (0.44, 0.043, 1), (0.50, 0.038, 0), (0.55, 0.034, 1)]
    ARM = [a for a in ARM if L0 or a[2]]
    actrl = [(-0.04, {"clavicle_l": 0.35, "upperarm_l": 0.65}), (0.03, {"upperarm_l": 1}), (0.255, {"upperarm_l": 1}),
             (0.29, {"upperarm_l": 0.5, "lowerarm_l": 0.5}), (0.325, {"lowerarm_l": 1}), (0.40, {"lowerarm_l": 1}),
             (0.50, {"lowerarm_l": 0.4, "lowerarm_twist_01_l": 0.6}), (0.56, {"lowerarm_twist_01_l": 0.6, "hand_l": 0.4})]
    out = Vector((0.71, 0, 0.70))
    rings = []
    for s_, r, _ in ARM:
        p, tan = polyline_at(path, s_)
        rings.append((ring(p, tan, out, NL, r, r, r * 0.96, r * 0.96), chain(s_, actrl), 0))
    loft(mb, rings, lambda i, j: SW["pink"] if j == 0 else (SW["cyan"] if j == 1 else SW["suit"]), PART["arm_l"])
    mb.end()
    # glove cuff
    mb.begin("cuff_l")
    CUFF = [(0.455, 0.0395), (0.468, 0.050), (0.49, 0.049), (0.53, 0.045), (0.565, 0.039)]
    if not L0: CUFF = [CUFF[0], CUFF[1], CUFF[3], CUFF[4]]
    rings = []
    for s_, r in CUFF:
        p, tan = polyline_at(path, s_)
        rings.append((ring(p, tan, out, NL, r), chain(s_, actrl), 0))
    nb = len(CUFF) - 1
    loft(mb, rings, lambda i, j: SW["pink"] if i < nb - 1 else SW["glove"], PART["cuff_l"])
    mb.end()
    # hand
    mb.begin("hand_l")
    a, n, t, WR = I["a"], I["n"], I["t"], I["WR"]
    hw = {"hand_l": 1}
    if L0:
        PALM = [(-0.005, 0.031, 0.019), (0.025, 0.039, 0.022), (0.055, 0.045, 0.021), (0.080, 0.046, 0.019),
                (0.092, 0.042, 0.014)]
        NP = 12
    else:
        PALM = [(-0.005, 0.030, 0.017), (0.035, 0.040, 0.019), (0.075, 0.044, 0.017), (0.12, 0.042, 0.014),
                (0.155, 0.034, 0.012), (0.172, 0.020, 0.008)]
        NP = 8
    rings = []
    for s_, wd, th in PALM:
        c = WR + a * s_ * HS + n * 0.002
        pts = ring(c, a, t, NP, wd * HS, wd * HS, th * HS, th * HS, 3.0)
        if L0: w = chain(s_, [(-0.005, {"hand_l": 0.6, "lowerarm_twist_01_l": 0.4}), (0.02, hw)])
        else: w = chain(s_, [(-0.005, {"hand_l": 0.6, "lowerarm_twist_01_l": 0.4}), (0.02, hw), (0.085, hw),
                             (0.13, {"middle_01_l": 0.4, "index_01_l": 0.2, "ring_01_l": 0.2, "pinky_01_l": 0.2})])
        rings.append((pts, w, 0 if L0 else 2 * (s_ > 0.1)))
    # which columns face the back of the hand (-n): those get the cyan knuckle panel on the last band
    yv = a.cross(t)
    backcols = {j for j in range(NP) if (t * cos(2 * pi * j / NP) + yv * sin(2 * pi * j / NP)).dot(-n) > 0.6}
    nbP = len(PALM) - 1
    kb = nbP - 2 if L0 else 2
    loft(mb, rings, lambda i, j: SW["knuckle"] if (i == kb and j in backcols) else SW["glove"], PART["hand_l"],
         cap1=(None if L0 else WR + a * 0.176 * HS, SW["glove"]))
    fingers = ("thumb", "index", "middle", "ring", "pinky") if L0 else ("thumb",)
    for fn in fingers:
        pts, r0 = I["fing"][fn]
        segL = [(pts[k + 1] - pts[k]).length for k in range(3)]; tot = sum(segL)
        if L0:
            SS = [(-0.012, 1.0), (0.0, 1.0), (segL[0], 0.94), (segL[0] + segL[1], 0.88), (tot - 0.0045 * HS, 0.78)]
            bones = ["%s_%02d_l" % (fn, k) for k in (1, 2, 3)]
            fctrl = [(-0.012, {"hand_l": 0.7, bones[0]: 0.3}), (0.004, {bones[0]: 1}), (segL[0] - 0.004, {bones[0]: 1}),
                     (segL[0] + 0.004, {bones[1]: 1}), (segL[0] + segL[1] - 0.004, {bones[1]: 1}),
                     (segL[0] + segL[1] + 0.004, {bones[2]: 1})]
            npf = 8
        else:
            if fn != "thumb": continue
            SS = [(-0.012, 1.0), (0.0, 1.0), (segL[0], 0.92), (tot - 0.006, 0.8)]
            fctrl = [(-0.012, {"hand_l": 0.7, "thumb_01_l": 0.3}), (0.004, {"thumb_01_l": 1}),
                     (segL[0] - 0.004, {"thumb_01_l": 1}), (segL[0] + 0.004, {"thumb_02_l": 1})]
            npf = 5
        rings = []
        for s_, k in SS:
            p, tan = polyline_at(pts, s_)
            rr = r0 * k
            rings.append((ring(p, tan, n, npf, rr * 0.96, rr * 0.96, rr, rr), chain(s_, fctrl), 2))
        tip, ttan = polyline_at(pts, tot + 0.004 * HS)
        loft(mb, rings, lambda i, j: SW["glove"], PART["hand_l"], cap1=(tip, SW["glove"]))
    mb.end()
    # leg
    mb.begin("leg_l")
    lpath = [I["HIP"], I["KN"], I["AN"]]
    LEG = [(-0.05, 0.074, 1), (0.0, 0.085, 1), (0.08, 0.089, 0), (0.17, 0.086, 1), (0.27, 0.079, 0), (0.35, 0.071, 1),
           (0.405, 0.065, 1), (0.44, 0.063, 1), (0.475, 0.063, 1), (0.53, 0.066, 0), (0.60, 0.066, 1), (0.68, 0.056, 0),
           (0.75, 0.045, 1), (0.81, 0.043, 1)]
    LEG = [l for l in LEG if L0 or l[2]]
    lctrl = [(-0.05, {"pelvis": 0.45, "thigh_l": 0.55}), (0.05, {"thigh_l": 1}), (0.40, {"thigh_l": 1}),
             (0.44, {"thigh_l": 0.5, "calf_l": 0.5}), (0.48, {"calf_l": 1})]
    rings = []
    for s_, r, _ in LEG:
        p, tan = polyline_at(lpath, s_)
        calf = 0.5 <= s_ <= 0.70
        # ring frame: x = outer (+X), y = axis x outer = front for a downward leg
        rings.append((ring(p, tan, Vector((1, 0, 0)), NL, r, r * 0.97, r * (1.03 if s_ < 0.4 else 1.0),
                           r * (1.10 if calf else 0.98)), chain(s_, lctrl), 0))
    loft(mb, rings, lambda i, j: SW["pink"] if j == 0 else (SW["cyan"] if j == 1 else SW["suit"]), PART["leg_l"])
    mb.end()
    # boot
    mb.begin("boot_l")
    AN, BALL = I["AN"], I["BALL"]; bx = AN.x
    BOOT = [(0.074, 0.026, 0.070), (0.066, 0.040, 0.100), (0.042, 0.048, 0.122), (0.0, 0.052, 0.132),
            (-0.05, 0.054, 0.112), (-0.10, 0.055, 0.088), (-0.14, 0.053, 0.072), (-0.175, 0.047, 0.060),
            (-0.198, 0.036, 0.048), (-0.212, 0.020, 0.034)]
    if not L0: BOOT = [BOOT[k] for k in (0, 1, 3, 4, 5, 6, 7, 9)]
    bctrl = [(-0.17, {"ball_l": 1}), (-0.135, {"foot_l": 0.5, "ball_l": 0.5}), (-0.10, {"foot_l": 1})]
    rings = []
    for y, rx, top in BOOT:
        cz = top * 0.45
        pts = ring(Vector((bx, y, cz)), Vector((0, -1, 0)), Vector((1, 0, 0)), NB, rx, rx, top * 0.55, cz * 1.6, 2.6, clampz=0.0)
        rings.append((pts, chain(y, bctrl), 0))
    ys = [b[0] for b in BOOT]

    def bsw(i, j):
        ang = 2 * pi * j / NB; up = sin(ang)
        if up < -0.55: return SW["sole"]
        if ys[i] >= 0.04: return SW["pink"]
        if -0.11 <= ys[i] < 0.0 and up > 0.5: return SW["strap"]
        return SW["boot"]
    loft(mb, rings, bsw, PART["boot_l"], cap0=(Vector((bx, 0.079, 0.036)), SW["pink"]),
         cap1=(Vector((bx, -0.217, 0.018)), SW["boot"]))
    # boot shaft
    SH_ = [(0.08, 0.046), (0.11, 0.053), (0.15, 0.055), (0.178, 0.055), (0.186, 0.056), (0.198, 0.054)]
    if not L0: SH_ = [SH_[0], SH_[2], SH_[4], SH_[5]]
    sctrl = [(0.05, {"foot_l": 0.7, "calf_l": 0.3}), (0.12, {"foot_l": 0.35, "calf_l": 0.65}), (0.19, {"calf_l": 1})]
    rings = []
    for z, r in SH_:
        rings.append((ring(Vector((bx, AN.y - 0.002, z)), Vector((0, 0, 1)), Vector((1, 0, 0)), NB, r), chain(z, sctrl), 0))
    nbs = len(SH_) - 1
    loft(mb, rings, lambda i, j: SW["pink"] if i >= nbs - (2 if L0 else 1) else SW["boot"], PART["boot_l"])
    mb.end()
    # mirror left -> right
    for p in ("deltoid", "arm", "cuff", "hand", "leg", "boot"):
        mb.mirror(p + "_l", p + "_r", PART[p + "_r"])
    return mb


def build_helmet(lod):
    L0 = lod == 0
    mbH = MB(helmet_uv); mbV = MB(helmet_uv)
    NC = 36 if L0 else 20
    HC = Vector((0, 0.004, 1.628))

    def hp(phi, psi, off=0.0):
        cx, cy = sin(phi), -cos(phi)
        rx, ry, rz = 0.130 + off, (0.148 if cy < 0 else 0.140) + off, 0.143 + off
        c, s = cos(psi), sin(psi)
        x, y, z = rx * c * cx, ry * c * cy, rz * s
        y -= 0.018 * max(0, -cy) ** 2 * max(0, -s)
        return HC + Vector((x, y, z))

    def psimin(phi): return radians(-49 - 14 * max(0, cos(phi)) ** 1.5)
    FIX = [-20, -8, 4, 14, 22, 30, 40, 52, 64, 76, 86] if L0 else [-20, 0, 22, 30, 48, 70, 86]
    hw = {"head": 1}
    mbH.begin("helmet")
    rows = []   # each row: list of points per column
    for kind in ("in2", "in1", "b0", "b1", "b2") + tuple(FIX):
        row = []
        for j in range(NC):
            ph = 2 * pi * j / NC; pm = psimin(ph)
            if kind == "in2": p = hp(ph, pm + radians(12), -0.024)
            elif kind == "in1": p = hp(ph, pm - radians(1), -0.011)
            elif kind == "b0": p = hp(ph, pm)
            elif kind == "b1": p = hp(ph, pm + (radians(-20) - pm) / 3)
            elif kind == "b2": p = hp(ph, pm + 2 * (radians(-20) - pm) / 3)
            else: p = hp(ph, radians(kind))
            row.append(p)
        if not L0 and kind in ("b2",): continue
        rows.append((kind, row))
    # rings are already ordered bottom (inside) -> top, columns CCW about +Z (phi 0 = front -Y, 90deg = +X)
    kinds = [k for k, _ in rows]
    def hsw(i, j):
        k0 = kinds[i]; ph = 2 * pi * (j + 0.5) / NC; aphi = math.degrees(min(ph, 2 * pi - ph))
        if k0 in ("in2", "in1"): return HSW["pad"]
        if k0 == 22 and aphi >= 72: return HGRAD
        return HSW["shell"]
    rings = [(row, hw, 0) for _, row in rows]
    nR = len(rings)
    ids = [[mbH.vert(p, hw, 0, 20) for p in row] for row in [r[0] for r in rings]]
    for i in range(nR - 1):
        for j in range(NC):
            j2 = (j + 1) % NC; sid = hsw(i, j)
            vids = [ids[i][j], ids[i][j2], ids[i + 1][j2], ids[i + 1][j]]
            if sid == HGRAD:
                def gu(jj):
                    ph = 2 * pi * jj / NC; aphi = math.degrees(min(ph, 2 * pi - ph)); return min(1, max(0, (aphi - 72) / 108))
                uvs = [helmet_uv(HGRAD, gu(j), 0.2), helmet_uv(HGRAD, gu(j + 1), 0.2), helmet_uv(HGRAD, gu(j + 1), 0.8), helmet_uv(HGRAD, gu(j), 0.8)]
            else:
                uvs = [helmet_uv(sid, jj / NC, ii / nR) for jj, ii in ((j, i), (j + 1, i), (j + 1, i + 1), (j, i + 1))]
            mbH.face(vids, uvs)
    top = mbH.vert(hp(0, radians(90)), hw, 0, 20); cu = helmet_uv(HSW["shell"], 0.5, 0.5)
    for j in range(NC):
        mbH.face([ids[-1][j], ids[-1][(j + 1) % NC], top], [cu, cu, cu])
    # inner opening: close the inside so nothing shows through the open bottom (dark pad disc above the head)
    # rear fin
    fb1, fb2, ft = hp(pi, radians(42)), hp(pi, radians(80)), hp(pi, radians(60), 0.030)
    ft.y += 0.012
    d = Vector((0.0055, 0, 0)); dt = Vector((0.003, 0, 0))
    P = [mbH.vert(p, hw, 0, 21) for p in (fb1 + d, ft + dt, fb2 + d, fb1 - d, ft - dt, fb2 - d)]
    fu = helmet_uv(HSW["fin"], 0.5, 0.5); fu2 = helmet_uv(HSW["fin"], 0.2, 0.2); fu3 = helmet_uv(HSW["fin"], 0.8, 0.3)
    mbH.face([P[0], P[1], P[2]], [fu, fu2, fu3]); mbH.face([P[3], P[5], P[4]], [fu, fu3, fu2])
    mbH.face([P[3], P[4], P[1], P[0]], [fu, fu2, fu3, fu]); mbH.face([P[4], P[5], P[2], P[1]], [fu, fu2, fu3, fu])
    mbH.end()
    # visor
    mbV.begin("visor")
    NV = 20 if L0 else 10
    VR = [-24, -20.5, -12, -4, 4, 12, 19, 22] if L0 else [-24, -20.5, 0, 19, 22]
    ids = []
    for k, ps in enumerate(VR):
        row = []
        for j in range(NV + 1):
            ph = radians(-74 + 148 * j / NV)
            # visor sits proud of the shell; its edges dip in a little so the plate looks inset
            edge = 0.0 if (k in (0, len(VR) - 1) or j in (0, NV)) else 0.0015
            row.append(mbV.vert(hp(ph, radians(ps), 0.0045 + edge), hw, 0, 22))
        ids.append(row)
    for i in range(len(VR) - 1):
        for j in range(NV):
            vids = [ids[i][j], ids[i][j + 1], ids[i + 1][j + 1], ids[i + 1][j]]
            sid = HSW["vedge"] if i == 0 else HSW["visor"]
            uvs = [helmet_uv(sid, jj / NV, ii / len(VR)) for jj, ii in ((j, i), (j + 1, i), (j + 1, i + 1), (j, i + 1))]
            mbV.face(vids, uvs)
    mbV.end()
    return mbH, mbV


# ================================================================================================== Blender objects
def mb_to_object(mb, name, arm_ob, mat_list, bone_names, sharp_parts=(), sharp_angle=50):
    me = bpy.data.meshes.new(name); bm = bmesh.new()
    vs = [bm.verts.new(c) for c in mb.co]; bm.verts.ensure_lookup_table()
    uvl = bm.loops.layers.uv.new("UVMap")
    for vids, uvs, slot in mb.faces:
        try:
            f = bm.faces.new([vs[i] for i in vids])
        except ValueError:
            continue
        f.material_index = slot; f.smooth = True
        for lp, uv in zip(f.loops, uvs): lp[uvl].uv = uv
    bm.edges.ensure_lookup_table()
    sp = set(sharp_parts)
    if sp:
        for e in bm.edges:
            if len(e.link_faces) == 2 and mb.part[e.verts[0].index] in sp:
                if e.link_faces[0].normal.angle(e.link_faces[1].normal, 0) > radians(sharp_angle): e.smooth = False
    bm.to_mesh(me); bm.free()
    a = me.attributes.new("dflags", "INT", "POINT"); a.data.foreach_set("value", mb.flag)
    a = me.attributes.new("dpart", "INT", "POINT"); a.data.foreach_set("value", mb.part)
    for m in mat_list: me.materials.append(m)
    ob = bpy.data.objects.new(name, me); bpy.context.scene.collection.objects.link(ob)
    groups = {b: ob.vertex_groups.new(name=b) for b in bone_names}
    for i, w in enumerate(mb.w):
        tot = sum(w.values())
        for b, x in w.items(): groups[b].add([i], x / tot, "REPLACE")
    hv = [i for i, f in enumerate(mb.flag) if f & 1]
    if hv: ob.vertex_groups.new(name="mask_head").add(hv, 1.0, "REPLACE")
    ob.parent = arm_ob
    mod = ob.modifiers.new("Armature", "ARMATURE"); mod.object = arm_ob
    return ob


def make_material(name, tex_img, emit_img, rough, metal, emit_strength=2.0, tint=(1, 1, 1)):
    m = bpy.data.materials.new(name)
    nt = m.node_tree; bsdf = nt.nodes.get("Principled BSDF")
    t = nt.nodes.new("ShaderNodeTexImage"); t.image = tex_img; t.interpolation = "Closest"
    if tint != (1, 1, 1):
        mix = nt.nodes.new("ShaderNodeMix"); mix.data_type = "RGBA"; mix.blend_type = "MULTIPLY"
        mix.inputs["Factor"].default_value = 1.0; mix.inputs["B"].default_value = (*tint, 1)
        nt.links.new(t.outputs["Color"], mix.inputs["A"]); nt.links.new(mix.outputs["Result"], bsdf.inputs["Base Color"])
    else:
        nt.links.new(t.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = rough; bsdf.inputs["Metallic"].default_value = metal
    if emit_img is not None:
        e = nt.nodes.new("ShaderNodeTexImage"); e.image = emit_img; e.interpolation = "Closest"
        nt.links.new(e.outputs["Color"], bsdf.inputs["Emission Color"]); bsdf.inputs["Emission Strength"].default_value = emit_strength
    m.diffuse_color = (0.1, 0.1, 0.2, 1)
    return m


def make_armature(B, order):
    arm = bpy.data.armatures.new("Armature"); ob = bpy.data.objects.new("Armature", arm)
    bpy.context.scene.collection.objects.link(ob)
    bpy.context.view_layer.objects.active = ob; ob.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    for nme in order:
        b = B[nme]; eb = arm.edit_bones.new(nme)
        eb.head, eb.tail = b["head"], b["tail"]; eb.align_roll(b["roll"])
        if b["parent"]: eb.parent = arm.edit_bones[b["parent"]]
        eb.use_connect = False
    bpy.ops.object.mode_set(mode="OBJECT")
    arm.display_type = "STICK"
    for pb in ob.pose.bones: pb.rotation_mode = "QUATERNION"
    return ob


# ================================================================================================== pose solver
class Pose:
    """Pose maths on armature-space matrices (same formula as Blender: W = Wparent @ Lrest @ basis)."""
    def __init__(s, arm_ob, order):
        s.order = order; s.bones = arm_ob.data.bones
        s.Wr = {b: s.bones[b].matrix_local.copy() for b in order}
        s.par = {b: (s.bones[b].parent.name if s.bones[b].parent else None) for b in order}
        s.Lr = {b: (s.Wr[s.par[b]].inverted() @ s.Wr[b] if s.par[b] else s.Wr[b].copy()) for b in order}
        s.basis = {b: Matrix.Identity(4) for b in order}

    def fk(s):
        W = {}
        for b in s.order:
            p = s.par[b]; W[b] = (W[p] @ s.Lr[b] if p else s.Lr[b]) @ s.basis[b]
        return W

    def set_world(s, b, M, W=None):
        W = W or s.fk(); p = s.par[b]
        base = (W[p] @ s.Lr[b]) if p else s.Lr[b]
        s.basis[b] = base.inverted() @ M

    def rotate_world(s, b, q, W=None):
        W = W or s.fk(); Wb = W[b]; h = Wb.translation.copy()
        M = Matrix.Translation(h) @ q.to_matrix().to_4x4() @ Matrix.Translation(-h) @ Wb
        s.set_world(b, M, W)

    def point(s, b, p_rest, W=None):
        W = W or s.fk(); return W[b] @ (s.Wr[b].inverted() @ p_rest)

    def head(s, b, W=None): return (W or s.fk())[b].translation.copy()

    def tail(s, b, W=None):
        W = W or s.fk(); return W[b] @ Vector((0, s.bones[b].length, 0))

    def apply(s, arm_ob):
        for b in s.order:
            pb = arm_ob.pose.bones[b]; pb.rotation_mode = "QUATERNION"; pb.matrix_basis = s.basis[b]


def ik2(P, b1, b2, b3, target, pole):
    W = P.fk(); A = P.head(b1, W); Bp = P.head(b2, W); Cp = P.head(b3, W)
    l1, l2 = (Bp - A).length, (Cp - Bp).length
    d = target - A; dist = min(max(d.length, 1e-4), (l1 + l2) * 0.999); dn = d.normalized()
    pv = pole - A; pn = (pv - dn * pv.dot(dn)).normalized()
    ca = max(-1, min(1, (l1 * l1 + dist * dist - l2 * l2) / (2 * l1 * dist))); ang = math.acos(ca)
    elbow = A + dn * (ca * l1) + pn * (math.sin(ang) * l1); end = A + dn * dist
    P.rotate_world(b1, (Bp - A).rotation_difference(elbow - A))
    # roll b1 about its axis so its X (hinge) axis is normal to the IK plane
    W = P.fk(); ax = (P.head(b2, W) - A).normalized(); nrm = (elbow - A).cross(end - elbow)
    if nrm.length > 1e-6:
        nrm.normalize(); X = W[b1].col[0].xyz.normalized()
        if X.dot(nrm) < 0: nrm = -nrm
        Xp = (X - ax * X.dot(ax)).normalized(); ang2 = Xp.angle(nrm, 0)
        if Xp.cross(nrm).dot(ax) < 0: ang2 = -ang2
        P.rotate_world(b1, Quaternion(ax, ang2))
    W = P.fk(); Bp = P.head(b2, W); Cp = P.head(b3, W)
    P.rotate_world(b2, (Cp - Bp).rotation_difference(end - Bp))
    return dist / (l1 + l2), (target - end).length


def char_from_unity(v): return Vector((-v[0], -v[2], v[1]))


def solve_seated(P, info, eye, seat_top, seat_back_y, wheel_pos, wX, wU, wF, rim_r, spin_deg=0.0, pelvis_tilt=-10.0,
                 log=None, rim_tube=0.014, leg_reach=0.86):
    """All inputs in character space (Blender, -Y forward). Same fit rules as the plugin spec (DRIVER spec section 4)."""
    P.basis = {b: Matrix.Identity(4) for b in P.order}
    socks = {s[0]: s for s in sockets_def(info)}
    hip_rest = socks["hip_c"][2]; eye_rest = socks["eye_c"][2]
    # pelvis: hip_c onto the seat, tilted back
    hip_t = Vector((eye.x, seat_back_y - 0.13, seat_top.z + 0.02))
    R = Matrix.Rotation(radians(pelvis_tilt), 3, "X")
    Wp = P.Wr["pelvis"]; ph0 = Wp.translation
    head_new = hip_t - R @ (hip_rest - ph0)
    P.set_world("pelvis", Matrix.Translation(head_new) @ (R @ Wp.to_3x3()).to_4x4())
    # spine: bring eye_c onto the eye (direction), spread 40/30/30
    for _ in range(6):
        W = P.fk(); e = P.point("head", eye_rest, W); piv = P.head("spine_01", W)
        q = (e - piv).rotation_difference(eye - piv); axis, ang = q.to_axis_angle()
        if abs(ang) < 1e-5: break
        for b, f in (("spine_01", 0.4), ("spine_02", 0.3), ("spine_03", 0.3)):
            P.rotate_world(b, Quaternion(axis, ang * f))
    # head level, neck halfway
    W = P.fk()
    qn = W["neck_01"].to_quaternion().slerp(P.Wr["neck_01"].to_quaternion(), 0.6)
    P.set_world("neck_01", Matrix.Translation(P.head("neck_01", W)) @ qn.to_matrix().to_4x4(), W)
    W = P.fk(); P.set_world("head", Matrix.Translation(P.head("head", W)) @ P.Wr["head"].to_3x3().to_4x4(), W)
    W = P.fk(); e = P.point("head", eye_rest, W)
    res = {"eye_err": (e - eye).length}
    torso = {b: P.basis[b].copy() for b in P.order}
    wn = wX.cross(wU).normalized()
    sp = radians(spin_deg)
    Xs = wX * cos(sp) + wU * sin(sp); Us = -wX * sin(sp) + wU * cos(sp)

    def rim_d(p):
        q = p - wheel_pos; h = q.dot(wn); rv = q - wn * h
        return math.sqrt((rv.length - rim_r) ** 2 + h * h)

    def arms(drop, prot, lean):
        """arms on the rim for a grip-angle drop (deg), clavicle protraction (m) and spine lean (deg); -> worst shortfall"""
        P.basis = {b: m.copy() for b, m in torso.items()}
        if lean > 0:
            for b, f in (("spine_01", 0.4), ("spine_02", 0.3), ("spine_03", 0.3)):
                P.rotate_world(b, Quaternion((1, 0, 0), radians(lean * f)))
            W = P.fk(); P.set_world("head", Matrix.Translation(P.head("head", W)) @ P.Wr["head"].to_3x3().to_4x4(), W)
        worst = 0.0; out = {}
        for sf, th in (("_l", 165.0 + drop), ("_r", 15.0 - drop)):
            I = info[sf]; sg = I["sg"]
            clen = P.bones["clavicle" + sf].length
            P.rotate_world("clavicle" + sf, Quaternion((0, 0, 1), -sg * (radians(6) + math.asin(min(0.9, prot / clen)))))
            P.rotate_world("clavicle" + sf, Quaternion((0, 1, 0), radians(sg * 4)))
            thr = radians(th)
            radial = Xs * cos(thr) + Us * sin(thr)
            grip = wheel_pos + radial * rim_r - wF * 0.01
            tang = (-Xs * sin(thr) + Us * cos(thr))            # CCW tangent
            z1 = tang if tang.dot(Us) > 0 else -tang            # toward 12 o'clock (pinky -> index points up the rim)
            y1 = (-radial + wF * 0.3); y1 = (y1 - z1 * y1.dot(z1)).normalized(); x1 = y1.cross(z1)
            S1 = Matrix((x1, y1, z1)).transposed()
            g = socks["grip" + sf]; z0 = g[3].normalized(); y0 = (g[4] - z0 * g[4].dot(z0)).normalized(); x0 = y0.cross(z0)
            S0 = Matrix((x0, y0, z0)).transposed()
            H0 = P.Wr["hand" + sf].to_3x3(); H1 = S1 @ (S0.inverted() @ H0)
            wrist = grip - H1 @ (H0.inverted() @ (g[2] - I["WR"]))
            W = P.fk(); sh = P.head("upperarm" + sf, W)
            pole = sh + Vector((sg * 0.35, -0.10, -0.5))
            reach, short = ik2(P, "upperarm" + sf, "lowerarm" + sf, "hand" + sf, wrist, pole)
            out["reach" + sf] = (reach, short); worst = max(worst, short)
            W = P.fk(); P.set_world("hand" + sf, Matrix.Translation(P.head("hand" + sf, W)) @ H1.to_4x4(), W)
            # half the forearm twist onto lowerarm_twist_01
            W = P.fk(); La = W["lowerarm" + sf].to_3x3(); ax = La.col[1].normalized()
            D = La.inverted() @ H1 @ (P.Wr["hand" + sf].to_3x3().inverted() @ P.Wr["lowerarm" + sf].to_3x3())
            qD = D.to_quaternion(); tw = 2 * math.atan2(qD.y, qD.w)
            if tw > pi: tw -= 2 * pi
            if tw < -pi: tw += 2 * pi
            P.rotate_world("lowerarm_twist_01" + sf, Quaternion(ax, 0.5 * tw), W)
            out["twist" + sf] = math.degrees(tw)
        return worst, out

    # reach order when short (spec): grip angle down to -30 deg, clavicle protraction 0.05 m, spine lean 10 deg
    prm = [0.0, 0.0, 0.0]
    for k, mx in ((0, 30.0), (1, 0.05), (2, 10.0)):
        if arms(*prm)[0] <= 0.002: break
        prm[k] = mx
        if arms(*prm)[0] > 0.002: continue
        lo, hi = 0.0, mx
        for _ in range(10):
            mid = 0.5 * (lo + hi); prm[k] = mid
            if arms(*prm)[0] <= 0.002: hi = mid
            else: lo = mid
        prm[k] = hi
    worst, out = arms(*prm); res.update(out)
    res["reach_steps"] = (round(prm[0], 1), round(prm[1], 3), round(prm[2], 1)); res["reach_left_cm"] = worst * 100
    # fingers wrap the rim: per joint, the smallest curl that brings the segment onto the rim surface
    for sf in ("_l", "_r"):
        I = info[sf]; grip_log = []
        for fn in ("index", "middle", "ring", "pinky", "thumb"):
            fr = I["fing"][fn][1]; target = rim_tube + fr * 0.95
            for k in (0, 1, 2):
                b = "%s_%02d%s" % (fn, k + 1, sf)
                W0 = P.fk(); X = W0[b].col[0].xyz.normalized(); keep = P.basis[b].copy(); best = None
                lim = ((40, 50, 50) if fn == "thumb" else (90, 100, 70))[k]
                for deg in range(0, lim + 1, 2):
                    P.basis[b] = keep.copy(); P.rotate_world(b, Quaternion(X, radians(deg)), W0)
                    W = P.fk(); hd = P.head(b, W); tl = P.tail(b, W)
                    ds = [rim_d(hd.lerp(tl, f)) for f in (0.5, 1.0)]
                    cost = sum((target - d_) * 8 if d_ < target else (d_ - target) for d_ in ds)
                    if best is None or cost < best[0] - 1e-9: best = (cost, deg)
                P.basis[b] = keep.copy(); P.rotate_world(b, Quaternion(X, radians(best[1])), W0)
                grip_log.append(best[1])
            W = P.fk(); tip = P.tail("%s_03%s" % (fn, sf), W)
            grip_log.append(round(rim_d(tip) * 1000))
        res["fingers" + sf] = grip_log
    # legs: heel on the floor, ankle at a reachable distance
    for sf in ("_l", "_r"):
        I = info[sf]; sg = I["sg"]
        W = P.fk(); hip = P.head("thigh" + sf, W)
        Lleg = (I["KN"] - I["HIP"]).length + (I["AN"] - I["KN"]).length
        az = seat_top.z - 0.40 + 0.075
        dz = hip.z - az; hor = math.sqrt(max(0.01, (leg_reach * Lleg) ** 2 - dz * dz))
        ank = Vector((hip.x + sg * 0.03, hip.y - hor, az))
        if sf == "_r": ank.y -= 0.02
        pole = hip + Vector((sg * 0.25, -1.0, 0.9))
        ik2(P, "thigh" + sf, "calf" + sf, "foot" + sf, ank, pole)
        W = P.fk(); rd = (I["BALL"] - I["AN"]).normalized()
        td = Vector((sg * 0.06, -cos(radians(18)), sin(radians(18)))).normalized()
        R = rd.rotation_difference(td).to_matrix() @ P.Wr["foot" + sf].to_3x3()
        P.set_world("foot" + sf, Matrix.Translation(P.head("foot" + sf, W)) @ R.to_4x4(), W)
        res["ankle" + sf] = ank
    if log is not None: log.update(res)
    return res


# ================================================================================================== export helpers
def to_u(M):
    R = C3 @ M.to_3x3() @ C3.transposed(); t = C3 @ M.translation
    out = R.to_4x4(); out.translation = t; return out


def q_xyzw(q): return (q.x, q.y, q.z, q.w)


def export_mesh_arrays(ob, bone_idx, rigid_bone_W=None):
    me = ob.data
    if hasattr(me, "calc_loop_triangles"): me.calc_loop_triangles()
    uvd = me.uv_layers.active.data; flags = me.attributes["dflags"].data
    gname = {g.index: g.name for g in ob.vertex_groups}
    key2i, pos, nrm, uv, bi, bw, fl = {}, [], [], [], [], [], []
    subs = {}
    inv = rigid_bone_W.inverted() if rigid_bone_W is not None else None
    for lt in me.loop_triangles:
        tri = []
        for li in (lt.loops[0], lt.loops[2], lt.loops[1]):         # det C = -1 -> flip winding (as DriverCam does)
            vi = me.loops[li].vertex_index; nn = me.corner_normals[li].vector; u = uvd[li].uv
            key = (vi, round(u.x, 5), round(u.y, 5), round(nn.x, 3), round(nn.y, 3), round(nn.z, 3))
            k = key2i.get(key)
            if k is None:
                k = len(pos); key2i[key] = k
                p = C3 @ me.vertices[vi].co; n_ = (C3 @ nn).normalized()
                if inv is not None: p = inv @ p; n_ = (inv.to_3x3() @ n_).normalized()
                pos.append(tuple(p)); nrm.append(tuple(n_)); uv.append((u.x, u.y)); fl.append(flags[vi].value)
                ws = sorted(((g.weight, gname[g.group]) for g in me.vertices[vi].groups
                             if gname[g.group] in bone_idx and g.weight > 1e-4), reverse=True)[:4]
                tot = sum(w for w, _ in ws) or 1.0
                b8 = [int(round(255 * w / tot)) for w, _ in ws]
                if b8: b8[0] += 255 - sum(b8)
                ids = [bone_idx[nm] for _, nm in ws]
                while len(b8) < 4: b8.append(0); ids.append(0)
                bi.append(tuple(ids)); bw.append(tuple(b8))
            tri.append(k)
        subs.setdefault(lt.material_index, []).extend(tri)
    idx, sublist = [], []
    for slot in sorted(subs):
        sublist.append((len(idx), len(subs[slot]), slot)); idx.extend(subs[slot])
    return dict(pos=pos, nrm=nrm, uv=uv, bi=bi, bw=bw, idx=idx, subs=sublist, flags=fl)


def key_pose(arm_ob, P, frame, action):
    arm_ob.animation_data.action = action
    for b in P.order:
        pb = arm_ob.pose.bones[b]; pb.matrix_basis = P.basis[b]
        pb.keyframe_insert("rotation_quaternion", frame=frame, group=b)
        pb.keyframe_insert("location", frame=frame, group=b)


def breathe(P, base, t):
    """additive breathing layer (bone-local deltas) applied on a copy of base basis; returns delta dict."""
    a = radians(0.6) * sin(2 * pi * t / 4.0); d = {}
    for b, ax, k in (("spine_02", "X", -1.0), ("spine_03", "X", -1.0), ("clavicle_l", "X", 1.0), ("clavicle_r", "X", 1.0)):
        d[b] = Quaternion(Vector((1, 0, 0)), a * k)
    return d


# ================================================================================================== props (preview only)
def make_props(eye, seat_top, seat_back_y, wheel_pos, wX, wU, wF, rim_r):
    coll = bpy.data.collections.new("Props"); bpy.context.scene.collection.children.link(coll)
    mat = bpy.data.materials.new("prop_grey"); mat.diffuse_color = (0.25, 0.25, 0.3, 1)
    mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.08, 0.08, 0.1, 1)
    mat.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.6

    def obj(name, bm):
        me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free(); me.materials.append(mat)
        o = bpy.data.objects.new(name, me); coll.objects.link(o)
        for p in me.polygons: p.use_smooth = True
        return o
    # wheel rim (torus) + 3 spokes + hub
    bm = bmesh.new(); NU, NV_ = 48, 10; tube = 0.014; vv = []
    for i in range(NU):
        a = 2 * pi * i / NU; rad = wX * cos(a) + wU * sin(a); c = wheel_pos + rad * rim_r
        vv.append([bm.verts.new(c + (rad * cos(2 * pi * k / NV_) + wF * sin(2 * pi * k / NV_)) * tube) for k in range(NV_)])
    for i in range(NU):
        for k in range(NV_):
            bm.faces.new([vv[i][k], vv[(i + 1) % NU][k], vv[(i + 1) % NU][(k + 1) % NV_], vv[i][(k + 1) % NV_]])
    for a in (0, 180, 270):
        ar = radians(a); rad = wX * cos(ar) + wU * sin(ar)
        r = bmesh.ops.create_cube(bm, size=1.0)
        M = Matrix((rad * rim_r, rad.cross(wF) * 0.035, wF * 0.012)).transposed().to_4x4()
        M.translation = wheel_pos + rad * rim_r * 0.5 + wF * 0.02
        bmesh.ops.transform(bm, matrix=M, verts=r["verts"])
    r = bmesh.ops.create_cone(bm, cap_ends=True, segments=16, radius1=0.05, radius2=0.04, depth=0.05)
    M = Matrix((wX, wU, wF)).transposed().to_4x4(); M.translation = wheel_pos + wF * 0.03
    bmesh.ops.transform(bm, matrix=M, verts=r["verts"])
    r = bmesh.ops.create_cone(bm, cap_ends=True, segments=12, radius1=0.025, radius2=0.025, depth=0.5)
    M = Matrix((wX, wU, wF)).transposed().to_4x4(); M.translation = wheel_pos + wF * 0.28
    bmesh.ops.transform(bm, matrix=M, verts=r["verts"])
    obj("prop_wheel", bm)
    # seat
    bm = bmesh.new()
    r = bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector((eye.x, seat_back_y - 0.26, seat_top.z - 0.06))) @
                        Matrix.Diagonal((0.52, 0.52, 0.12, 1)), verts=r["verts"])
    r = bmesh.ops.create_cube(bm, size=1.0)
    M = (Matrix.Translation(Vector((eye.x, seat_back_y + 0.05, seat_top.z + 0.38))) @ Matrix.Rotation(radians(-12), 4, "X") @
         Matrix.Diagonal((0.52, 0.09, 0.80, 1)))
    bmesh.ops.transform(bm, matrix=M, verts=r["verts"])
    bmesh.ops.bevel(bm, geom=list(bm.edges), offset=0.02, segments=2, affect="EDGES")
    obj("prop_seat", bm)
    # floor
    bm = bmesh.new(); r = bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=1.0)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector((eye.x, -0.4, seat_top.z - 0.405))) @ Matrix.Diagonal((0.8, 1.0, 1, 1)), verts=r["verts"])
    obj("prop_floor", bm)
    return coll


# ================================================================================================== main
GENERIC = dict(eye_u=(0.0, 1.20, 0.0), wheel_rel=(0.03, -0.32, 0.45), seat_rel=(0.0, -0.74, 0.08), back_rel=-0.14,
               F=(0.0, -0.34, 0.94), U=(0.0, 0.94, 0.34), rim=0.185)


def generic_targets(g=GENERIC):
    e = Vector(g["eye_u"])
    eye = char_from_unity(e)
    wheel = char_from_unity(e + Vector(g["wheel_rel"]))
    seat = char_from_unity(e + Vector(g["seat_rel"]))
    back_y = char_from_unity(e + Vector((0, 0, g["back_rel"]))).y
    F = char_from_unity(Vector(g["F"]).normalized()); U = char_from_unity(Vector(g["U"]).normalized())
    X = char_from_unity(Vector(g["U"]).cross(Vector(g["F"])).normalized())
    return eye, seat, back_y, wheel, X, U, F, g["rim"]


def main(outdir, do_fbx=True):
    os.makedirs(outdir, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene; sc.unit_settings.system = "METRIC"; sc.unit_settings.scale_length = 1.0
    B, info = skeleton_def(); order = info["order"]; bidx = {b: i for i, b in enumerate(order)}
    imgs = make_atlases(outdir)
    arm_ob = make_armature(B, order)
    m_suit = make_material("M_Driver_Suit", imgs["driver_atlas.png"], imgs["driver_emit.png"], 0.6, 0.0)
    m_helm = make_material("M_Driver_Helmet", imgs["driver_helmet.png"], imgs["driver_helmet_emit.png"], 0.12, 0.1)
    m_visor = make_material("M_Driver_Visor", imgs["driver_helmet.png"], imgs["driver_helmet_emit.png"], 0.05, 0.3)
    objs = {}
    for lod in (0, 1):
        mb = build_body(B, info, lod)
        objs["body%d" % lod] = mb_to_object(mb, "Body_LOD%d" % lod, arm_ob, [m_suit], order,
                                            sharp_parts=(PART["boot_l"], PART["boot_r"]), sharp_angle=55)
        mh, mv = build_helmet(lod)
        objs["helmet%d" % lod] = mb_to_object(mh, "Helmet_LOD%d" % lod, arm_ob, [m_helm], ["head"], sharp_parts=(21,), sharp_angle=40)
        objs["visor%d" % lod] = mb_to_object(mv, "Visor_LOD%d" % lod, arm_ob, [m_visor], ["head"])
    for k in ("body1", "helmet1", "visor1"): objs[k].hide_render = True; objs[k].hide_set(True)
    # ---------------------------------------------------------------- poses
    P = Pose(arm_ob, order)
    eye, seat, back_y, wheel, X, U, F, rim = generic_targets()
    fitlog = {}
    solve_seated(P, info, eye, seat, back_y, wheel, X, U, F, rim, log=fitlog)
    seated = {b: m.copy() for b, m in P.basis.items()}
    make_props(eye, seat, back_y, wheel, X, U, F, rim)
    arm_ob.animation_data_create()
    acts = {}
    for nm in ("A_Pose", "Seated_Drive", "Seated_Breathe"):
        a = bpy.data.actions.new(nm); a.use_fake_user = True; acts[nm] = a
    P.basis = {b: Matrix.Identity(4) for b in order}
    key_pose(arm_ob, P, 0, acts["A_Pose"]); key_pose(arm_ob, P, 1, acts["A_Pose"])
    P.basis = dict(seated); key_pose(arm_ob, P, 0, acts["Seated_Drive"]); key_pose(arm_ob, P, 1, acts["Seated_Drive"])
    for fr in range(0, 121, 4):
        d = breathe(P, seated, fr / 30.0)
        P.basis = {b: (seated[b] @ d[b].to_matrix().to_4x4() if b in d else seated[b]) for b in order}
        key_pose(arm_ob, P, fr, acts["Seated_Breathe"])
    P.basis = dict(seated)
    arm_ob.animation_data.action = acts["Seated_Drive"]
    P.apply(arm_ob)
    # ---------------------------------------------------------------- runtime files
    Wr_u = {b: to_u(arm_ob.data.bones[b].matrix_local) for b in order}
    skel = []
    for b in order:
        p = arm_ob.data.bones[b].parent
        L = Wr_u[p.name].inverted() @ Wr_u[b] if p else Wr_u[b]
        skel.append((b, bidx[p.name] if p else -1, tuple(L.translation), q_xyzw(L.to_quaternion().normalized()), (1.0, 1.0, 1.0)))
    socks = []
    sj = {}
    for nm, bone, pos, zd, yd in sockets_def(info):
        Z = (C3 @ zd).normalized(); Y = (C3 @ yd); Y = (Y - Z * Y.dot(Z)).normalized(); Xv = Y.cross(Z)
        Mw = Matrix((Xv, Y, Z)).transposed().to_4x4(); Mw.translation = C3 @ pos
        L = Wr_u[bone].inverted() @ Mw
        socks.append((nm, bidx[bone], tuple(L.translation), q_xyzw(L.to_quaternion().normalized())))
        sj[nm] = dict(bone=bone, unity_local_pos=list(L.translation), unity_local_rot_xyzw=list(q_xyzw(L.to_quaternion())),
                      blender_rest_pos=list(pos))
    mats = [dict(name="suit", tex="driver_atlas.png", color=(1, 1, 1, 1), smooth=0.4, metal=0.0, emission=(0.8, 0.8, 0.8),
                 emit_tex="driver_emit.png", flags=1),
            dict(name="helmet", tex="driver_helmet.png", color=(1, 1, 1, 1), smooth=0.85, metal=0.1, emission=(0.8, 0.8, 0.8),
                 emit_tex="driver_helmet_emit.png", flags=1),
            dict(name="visor", tex="driver_helmet.png", color=(1, 1, 1, 1), smooth=0.95, metal=0.3, emission=(1, 1, 1),
                 emit_tex="driver_helmet_emit.png", flags=0)]
    # meshes are exported from the rest pose
    arm_ob.data.pose_position = "REST"; bpy.context.view_layer.update()
    meshes, masks, rigids, counts = [], [], [], {}
    for lod in (0, 1):
        m = export_mesh_arrays(objs["body%d" % lod], bidx); m["lod"] = lod
        meshes.append(m); masks.append((lod, m["flags"])); counts["body%d" % lod] = (len(m["pos"]), len(m["idx"]) // 3)
        for part, slot in (("helmet", 1), ("visor", 2)):
            r = export_mesh_arrays(objs["%s%d" % (part, lod)], {"head": bidx["head"]}, rigid_bone_W=Wr_u["head"])
            rigids.append(dict(name=part + ("" if lod == 0 else "_lod1"), bone=bidx["head"], slot=slot, pos=r["pos"],
                               nrm=r["nrm"], uv=r["uv"], idx=r["idx"]))
            counts["%s%d" % (part, lod)] = (len(r["pos"]), len(r["idx"]) // 3)
    arm_ob.data.pose_position = "POSE"
    src = open(os.path.abspath(__file__), "rb").read()
    sha = hashlib.sha1(src).hexdigest()[:10]
    meta = [("units", "m"), ("height", "1.76"), ("seatedEye", "0.74"), ("forward", "+Z"), ("generator", "build_driver.py " + sha),
            ("license", "original"), ("rest", "A-pose"), ("grip", "+Z pinky->index (align to rim tangent toward 12 o'clock), +Y palm normal"),
            ("lods", "0 body+fingers, 1 mitten; RIGD *_lod1 for LOD1")]
    drm_size = drm_io.write_drm(os.path.join(outdir, "driver.drm"), skel, socks, mats, meshes, masks, rigids, meta)
    # clips
    def local_u(basis):
        P.basis = basis; W = P.fk(); Wu = {b: to_u(W[b]) for b in order}; out = {}
        for b in order:
            p = P.par[b]; L = Wu[p].inverted() @ Wu[b] if p else Wu[b]
            out[b] = (q_xyzw(L.to_quaternion().normalized()), tuple(L.translation))
        return out
    lu = local_u(seated)
    tracks = [(b, 3 if b == "pelvis" else 1) for b in order]
    clip_base = dict(name="seated_base", fps=30.0, flags=4, tracks=tracks,
                     frames=[[(lu[b][0], lu[b][1] if ch & 2 else None) for b, ch in tracks]])
    btr = [("spine_02", 1), ("spine_03", 1), ("clavicle_l", 1), ("clavicle_r", 1)]
    bfr = []
    for f in range(60):
        d = breathe(P, seated, f / 15.0)
        bfr.append([(q_xyzw((C3 @ d[b].to_matrix() @ C3.transposed()).to_quaternion().normalized()), None) for b, _ in btr])
    clip_br = dict(name="breathe_add", fps=15.0, flags=1 | 2, tracks=btr, frames=bfr)
    # sport-bike riding pose preset (plain Python in Unity space, the plugin's Solver.FitBike rules; anim_clips.py)
    ride_sk = anim_clips.Skel([b[0] for b in skel], [b[1] for b in skel], [b[2] for b in skel], [b[3] for b in skel])
    clip_ride, _, ride_rep = anim_clips.ride_clip(ride_sk, {n: (b, tuple(p), tuple(q)) for n, b, p, q in socks})
    fitlog["ride"] = {k: v for k, v in ride_rep.items() if k in ("eye", "torso_deg", "arm_l", "leg_l")}
    # (the 12 Unreal clips are added by fbx_to_dra.py, which keeps these three)
    dra_size = drm_io.write_dra(os.path.join(outdir, "driver_anims.dra"), [clip_base, clip_br, clip_ride])
    json.dump(dict(note="Sockets for the Driver character (not bones). Unity-space bone-local frames; blender_rest_pos in "
                        "Blender metres, character facing -Y.", sockets=sj),
              open(os.path.join(outdir, "driver_sockets.json"), "w"), indent=1)
    # ---------------------------------------------------------------- FBX for Unreal
    if do_fbx:
        bpy.ops.object.select_all(action="DESELECT")
        parts = [objs["body0"], objs["helmet0"], objs["visor0"]]
        dup = []
        for o in parts:
            d = o.copy(); d.data = o.data.copy(); bpy.context.scene.collection.objects.link(d); dup.append(d)
        for d in dup: d.select_set(True)
        bpy.context.view_layer.objects.active = dup[0]
        bpy.ops.object.join(); sk = bpy.context.view_layer.objects.active; sk.name = "SK_Driver"; sk.data.name = "SK_Driver"
        if "mask_head" in sk.vertex_groups: sk.vertex_groups.remove(sk.vertex_groups["mask_head"])
        for nm in ("dflags", "dpart"):
            if nm in sk.data.attributes: sk.data.attributes.remove(sk.data.attributes[nm])
        bpy.ops.object.select_all(action="DESELECT"); sk.select_set(True); arm_ob.select_set(True)
        bpy.context.view_layer.objects.active = arm_ob
        arm_ob.animation_data.action = None
        for b in order: arm_ob.pose.bones[b].matrix_basis = Matrix.Identity(4)
        bpy.ops.export_scene.fbx(filepath=os.path.join(outdir, "driver.fbx"), use_selection=True,
                                 object_types={"ARMATURE", "MESH"}, add_leaf_bones=False, primary_bone_axis="Y",
                                 secondary_bone_axis="X", apply_scale_options="FBX_SCALE_UNITS", mesh_smooth_type="FACE",
                                 use_armature_deform_only=False, bake_anim=True, bake_anim_use_all_actions=True,
                                 bake_anim_use_nla_strips=False, bake_anim_force_startend_keying=True,
                                 bake_anim_simplify_factor=0.0, path_mode="STRIP")
        bpy.data.objects.remove(sk, do_unlink=True)
        arm_ob.animation_data.action = acts["Seated_Drive"]; P.basis = dict(seated); P.apply(arm_ob)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(outdir, "driver.blend"), compress=True)
    tris0 = counts["body0"][1] + counts["helmet0"][1] + counts["visor0"][1]
    tris1 = counts["body1"][1] + counts["helmet1"][1] + counts["visor1"][1]
    print("DRIVER_BUILD counts", json.dumps(counts))
    print("DRIVER_BUILD LOD0 tris %d, LOD1 tris %d, bones %d, drm %d bytes, dra %d bytes" % (tris0, tris1, len(order), drm_size, dra_size))
    def fmt(v):
        if isinstance(v, (tuple, list, Vector)): return [fmt(x) for x in v]
        return round(v, 3) if isinstance(v, float) else v
    print("DRIVER_BUILD fit", {k: fmt(v) for k, v in fitlog.items()})


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = HERE
    if "--out" in argv: out = argv[argv.index("--out") + 1]
    main(os.path.abspath(out), do_fbx="--no-fbx" not in argv)
