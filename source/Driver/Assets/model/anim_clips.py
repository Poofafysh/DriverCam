"""Driver animation clips: original keyframed motion on the Driver's own 55-bone skeleton (no Mannequin, no sample or
store content). Plain Python (no bpy, no unreal), so the same definitions drive every tool:

    ue_anims.py     (inside Unreal Engine 5.8)  builds an AnimSequence per clip on the imported driver.fbx, exports FBX
    fbx_to_dra.py   (inside Blender)            reads those FBX files back and writes driver_anims.dra for the plugin
    python anim_clips.py driver.drm             self-check: evaluates every clip in Unity space, prints a summary

How a clip is written: keys at times (seconds), each key a dict of channels; between keys each channel follows a cubic
(Catmull-Rom) curve, wrapped for loops. A channel missing from a key is 0 there (aim weight 0). Channels:

    "bone:X>Y"     degrees: turn the bone in the plane from direction X toward direction Y (character directions
                   U up, D down, F forward, B back, L left, R right; the character's own left / right). Applied about the
                   bone's joint, after its parent has moved. Being a plane, it means the same motion in any coordinate
                   system (Unity is mirrored against Blender / Unreal; an axis + angle would flip sign there).
    "bone:aim"     (r, u, f, w): point the bone (joint -> its tip bone) toward the character direction r R + u U + f F,
                   blended by w (0 = untouched, 1 = fully aimed). Applied before the plane turns of that bone.
    "pelvis:pos"   (r, u, f) metres: pelvis offset in character directions.

The character directions come from the rest skeleton in whatever space the tool works in: R from upperarm_l to
upperarm_r, U from pelvis to head, F from foot_r to ball_r (each made perpendicular to the ones before).

Bases: "seated" = the generic seated drive pose (clip seated_base / action Seated_Drive), "standing" = the rest A-pose.
Modes (what the plugin does with the clip; written into driver_anims.dra by fbx_to_dra.py):
    additive  stored as bone-local deltas against the base (local = base * delta); layered on the fitted pose
    pose      stored as full local rotations (rotation-only); used for the arm while a one-shot plays (shift, celebrate)
    full      stored as full local rotations + pelvis position (standing clips for the outside character)
"""
import math

DIRS = {"U": (0, 1, 0), "D": (0, -1, 0), "F": (0, 0, 1), "B": (0, 0, -1), "L": (-1, 0, 0), "R": (1, 0, 0)}   # (r, u, f)

# the bone a bone points at (for "aim")
TIP = {"clavicle": "upperarm", "upperarm": "lowerarm", "lowerarm": "hand", "hand": "middle_01", "thigh": "calf",
       "calf": "foot", "foot": "ball", "spine_01": "spine_02", "spine_02": "spine_03", "spine_03": "neck_01",
       "neck_01": "head", "pelvis": "spine_01"}

# bones the plugin's two-bone IK owns while the hands are on the rim (an additive seated clip never moves them)
IK_ARM = ("upperarm", "lowerarm", "lowerarm_twist_01", "hand")


# ------------------------------------------------------------------------------------------------ quaternion maths (xyzw)
def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def qinv(q): return (-q[0], -q[1], -q[2], q[3])


def qnorm(q):
    n = math.sqrt(sum(c * c for c in q)) or 1.0
    return tuple(c / n for c in q)


def qrot(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + y * tz - z * ty, vy + w * ty + z * tx - x * tz, vz + w * tz + x * ty - y * tx)


def qaxis(axis, deg):
    a = math.radians(deg) * 0.5; s = math.sin(a)
    return qnorm((axis[0] * s, axis[1] * s, axis[2] * s, math.cos(a)))


def qslerp_id(q, t):
    """identity -> q by t (shortest way)."""
    if q[3] < 0: q = tuple(-c for c in q)
    ang = 2 * math.acos(max(-1.0, min(1.0, q[3])))
    s = math.sqrt(max(0.0, 1 - q[3] * q[3]))
    if s < 1e-7 or ang < 1e-7: return (0.0, 0.0, 0.0, 1.0)
    return qaxis((q[0] / s, q[1] / s, q[2] / s), math.degrees(ang * t))


def vadd(a, b): return tuple(x + y for x, y in zip(a, b))
def vsub(a, b): return tuple(x - y for x, y in zip(a, b))
def vmul(a, k): return tuple(x * k for x in a)
def vdot(a, b): return sum(x * y for x, y in zip(a, b))
def vcross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def vnorm(a):
    n = math.sqrt(vdot(a, a)) or 1.0
    return tuple(x / n for x in a)


def qfromto(a, b):
    a = vnorm(a); b = vnorm(b); d = vdot(a, b)
    if d > 0.999999: return (0.0, 0.0, 0.0, 1.0)
    if d < -0.999999:
        ax = vcross(a, (1, 0, 0))
        if vdot(ax, ax) < 1e-8: ax = vcross(a, (0, 1, 0))
        return qaxis(vnorm(ax), 180.0)
    c = vcross(a, b)
    return qnorm((c[0], c[1], c[2], 1 + d))


# ------------------------------------------------------------------------------------------------ skeleton in some space
class Skel:
    """names / parents (parents first) and a base pose (local rotations xyzw + local positions) in one tool's space.
    unit = metres per tool unit (1 for Unity / Blender, 0.01 for Unreal centimetres)."""

    def __init__(s, names, parents, rest_lp, rest_lq, unit=1.0):
        s.names = list(names); s.par = list(parents); s.idx = {n: i for i, n in enumerate(s.names)}
        s.rest_lp = [tuple(p) for p in rest_lp]; s.rest_lq = [qnorm(q) for q in rest_lq]; s.unit = unit
        wp, _ = s.fk(s.rest_lp, s.rest_lq)
        P = lambda n: wp[s.idx[n]]
        R = vnorm(vsub(P("upperarm_r"), P("upperarm_l")))
        U = vsub(P("head"), P("pelvis")); U = vnorm(vsub(U, vmul(R, vdot(U, R))))
        F = vsub(P("ball_r"), P("foot_r")); F = vsub(F, vmul(R, vdot(F, R))); F = vnorm(vsub(F, vmul(U, vdot(F, U))))
        s.axes = (R, U, F)
        s.tip = {}
        for i, n in enumerate(s.names):
            stem = n[:-2] if n.endswith(("_l", "_r")) else n
            side = n[-2:] if n.endswith(("_l", "_r")) else ""
            t = TIP.get(stem)
            j = s.idx.get(t + side) if t else None
            if j is None: j = next((k for k in range(i + 1, len(s.names)) if s.par[k] == i), None)
            s.tip[i] = j

    def dir(s, r, u, f):
        R, U, F = s.axes
        return vadd(vadd(vmul(R, r), vmul(U, u)), vmul(F, f))

    def fk(s, lp, lq):
        wp, wq = [None] * len(s.names), [None] * len(s.names)
        for i, p in enumerate(s.par):
            if p < 0: wp[i], wq[i] = lp[i], lq[i]
            else: wq[i] = qnorm(qmul(wq[p], lq[i])); wp[i] = vadd(wp[p], qrot(wq[p], lp[i]))
        return wp, wq


# ------------------------------------------------------------------------------------------------ curves
def _interp(times, vals, t, loop, length):
    """Catmull-Rom through (times, vals) at t; vals are floats. A loop wraps over length (key 0 again at length)."""
    n = len(times)
    if n == 1: return vals[0]
    if loop:
        t = t % length
        def at(k):
            return times[k % n] + (k // n) * length, vals[k % n]
        i = max(k for k in range(n) if times[k] <= t + 1e-9)
    else:
        t = max(times[0], min(times[-1], t))
        def at(k):
            k = max(0, min(n - 1, k)); return times[k], vals[k]
        i = max(k for k in range(n - 1) if times[k] <= t + 1e-9)
    t0, v0 = at(i); t1, v1 = at(i + 1); tm, vm = at(i - 1); tp, vp = at(i + 2)
    h = (t1 - t0) or 1e-6
    m0 = (v1 - vm) / (t1 - tm) * h if t1 - tm > 1e-6 and tm < t0 else (v1 - v0)
    m1 = (vp - v0) / (tp - t0) * h if tp - t0 > 1e-6 and tp > t1 else (v1 - v0)
    u = (t - t0) / h; u2 = u * u; u3 = u2 * u
    return (2 * u3 - 3 * u2 + 1) * v0 + (u3 - 2 * u2 + u) * m0 + (-2 * u3 + 3 * u2) * v1 + (u3 - u2) * m1


def channels(clip):
    """every channel used by the clip, in a fixed order (aim before planes for a bone)."""
    seen = []
    for _, k in clip["keys"]:
        for c in k:
            if c not in seen: seen.append(c)
    return sorted(seen, key=lambda c: (c.split(":")[0], 0 if c.endswith(":aim") or c.endswith(":pos") else 1, seen.index(c)))


def sample(clip, t):
    """channel values at time t: {channel: float or tuple}."""
    times = [k[0] for k in clip["keys"]]
    out = {}
    for c in channels(clip):
        size = 4 if c.endswith(":aim") else 3 if c.endswith(":pos") else 1
        comps = []
        for j in range(size):
            vals = []
            for _, k in clip["keys"]:
                v = k.get(c)
                if v is None: v = 0.0 if size == 1 else (0.0,) * size
                vals.append(v if size == 1 else v[j])
            comps.append(_interp(times, vals, t, clip.get("loop", False), clip["length"]))
        out[c] = comps[0] if size == 1 else tuple(comps)
    return out


# ------------------------------------------------------------------------------------------------ evaluation
def pose(clip, t, sk, base_lp, base_lq):
    """the clip's full local pose (lp, lq) at time t in sk's space, on the given base pose."""
    ch = sample(clip, t)
    lp = list(base_lp); lq = list(base_lq)
    per = {}
    for c, v in ch.items():
        b, kind = c.split(":", 1); per.setdefault(b, []).append((kind, v))
    wp, wq = [None] * len(sk.names), [None] * len(sk.names)
    for i, n in enumerate(sk.names):
        p = sk.par[i]
        if n in per:
            Wc = qnorm(qmul(wq[p], lq[i])) if p >= 0 else lq[i]
            Rq = (0.0, 0.0, 0.0, 1.0)
            for kind, v in per[n]:
                if kind == "pos":
                    off = vmul(sk.dir(*v), 1.0 / sk.unit)
                    lp[i] = vadd(lp[i], qrot(qinv(wq[p]), off) if p >= 0 else off)
                elif kind == "aim":
                    r, u, f, w = v
                    j = sk.tip.get(i)
                    if j is None or w <= 1e-4 or (abs(r) + abs(u) + abs(f)) < 1e-6: continue
                    cur = qrot(qmul(Rq, Wc), base_lp[j])
                    Rq = qmul(qslerp_id(qfromto(cur, sk.dir(r, u, f)), min(1.0, w)), Rq)
                else:
                    a, b_ = kind.split(">")
                    if abs(v) < 1e-6: continue
                    u_ = sk.dir(*DIRS[a]); v_ = sk.dir(*DIRS[b_])
                    Rq = qmul(qaxis(vnorm(vcross(u_, v_)), v), Rq)
            Wn = qnorm(qmul(Rq, Wc))
            lq[i] = qnorm(qmul(qinv(wq[p]), Wn)) if p >= 0 else Wn
        if p < 0: wp[i], wq[i] = lp[i], lq[i]
        else: wq[i] = qnorm(qmul(wq[p], lq[i])); wp[i] = vadd(wp[p], qrot(wq[p], lp[i]))
    return lp, lq


def frames(clip):
    """frame count at the clip's fps (the last frame lands on the length; a loop leaves it out: it equals frame 0)."""
    n = int(round(clip["length"] * clip["fps"]))
    return n if clip.get("loop") else n + 1


# ------------------------------------------------------------------------------------------------ mirroring
def _flip_dir(s): return s.replace("L", "#").replace("R", "L").replace("#", "R")


def _flip_bone(b):
    if b.endswith("_l"): return b[:-2] + "_r"
    if b.endswith("_r"): return b[:-2] + "_l"
    return b


def mirror(clip, name):
    keys = []
    for t, k in clip["keys"]:
        nk = {}
        for c, v in k.items():
            b, kind = c.split(":", 1)
            if kind == "aim": nv = (-v[0], v[1], v[2], v[3])
            elif kind == "pos": nv = (-v[0], v[1], v[2])
            else: kind = _flip_dir(kind); nv = v
            nk[_flip_bone(b) + ":" + kind] = nv
        keys.append((t, nk))
    return dict(clip, name=name, keys=keys)


# ------------------------------------------------------------------------------------------------ the clips
def _arms_down(k=None, w=1.0):
    """standing: both arms hanging, a little out from the body and the elbows slightly bent."""
    d = dict(k or {})
    d.setdefault("upperarm_l:aim", (-0.16, -1.0, 0.02, w)); d.setdefault("upperarm_r:aim", (0.16, -1.0, 0.02, w))
    d.setdefault("lowerarm_l:aim", (-0.05, -1.0, 0.16, w)); d.setdefault("lowerarm_r:aim", (0.05, -1.0, 0.16, w))
    return d


IDLE_SEATED = dict(name="idle_seated", base="seated", mode="additive", fps=15.0, length=4.0, loop=True, keys=[
    (0.0, {}),
    (1.0, {"spine_02:F>U": 0.6, "spine_03:F>U": 0.6, "clavicle_l:L>U": 1.0, "clavicle_r:R>U": 1.0,
           "head:F>L": 1.5, "head:F>U": 0.6, "neck_01:F>L": 0.6}),
    (2.0, {"spine_02:F>U": 1.0, "spine_03:F>U": 1.0, "clavicle_l:L>U": 1.6, "clavicle_r:R>U": 1.6,
           "head:F>L": 0.4, "head:F>U": -0.4}),
    (3.0, {"spine_02:F>U": 0.4, "spine_03:F>U": 0.4, "clavicle_l:L>U": 0.6, "clavicle_r:R>U": 0.6,
           "head:F>R": 1.8, "neck_01:F>R": 0.7, "head:U>R": 0.6}),
])

# left turn at full lock: shoulders turn and lean into the corner; the arms go hand over hand (the plugin's IK keeps
# the hands on the rim, so in game only the torso / neck / head part is layered, scrubbed by how far you steer)
STEER_LEFT = dict(name="steer_left", base="seated", mode="additive", fps=30.0, length=1.2, keys=[
    (0.0, {}),
    (0.35, {"spine_02:F>L": 1.5, "spine_03:F>L": 2.5, "spine_03:U>L": 1.5, "head:U>L": 1.5,
            "upperarm_r:F>U": 18, "upperarm_r:R>L": 22, "lowerarm_r:F>L": 25, "hand_r:F>L": 10,
            "upperarm_l:F>D": 10, "lowerarm_l:F>D": 12}),
    (0.7, {"spine_02:F>L": 2.5, "spine_03:F>L": 4.0, "spine_03:U>L": 3.0, "head:U>L": 3.0, "neck_01:F>L": 2.0,
           "upperarm_r:F>U": 10, "upperarm_r:R>L": 30, "lowerarm_r:F>L": 35, "hand_r:F>L": 12,
           "upperarm_l:F>U": 14, "upperarm_l:F>R": 10, "lowerarm_l:F>D": 6}),
    (1.2, {"spine_02:F>L": 3.0, "spine_03:F>L": 5.0, "spine_03:U>L": 4.0, "head:U>L": 4.0, "neck_01:F>L": 3.0,
           "spine_01:U>L": 1.0, "upperarm_r:R>L": 26, "lowerarm_r:F>L": 30, "hand_r:F>L": 8,
           "upperarm_l:F>D": 6, "lowerarm_l:F>D": 8}),
])
STEER_RIGHT = mirror(STEER_LEFT, "steer_right")

# right hand from the wheel to a gear lever by the right knee, a push, and back (in game: the right arm's own rotations,
# blended in and out over the IK)
SHIFT = dict(name="shift", base="seated", mode="pose", fps=30.0, length=1.0, keys=[
    (0.0, {}),
    (0.25, {"clavicle_r:U>D": 3, "upperarm_r:aim": (0.35, -0.85, 0.35, 1.0), "lowerarm_r:aim": (0.15, -0.55, 0.82, 1.0),
            "hand_r:F>D": 18, "spine_03:F>R": 2, "head:F>R": 4}),
    (0.42, {"clavicle_r:U>D": 3, "upperarm_r:aim": (0.35, -0.88, 0.25, 1.0), "lowerarm_r:aim": (0.12, -0.75, 0.65, 1.0),
            "hand_r:F>D": 22, "spine_03:F>R": 2, "head:F>R": 5}),
    (0.6, {"clavicle_r:U>D": 2, "upperarm_r:aim": (0.35, -0.85, 0.35, 1.0), "lowerarm_r:aim": (0.15, -0.55, 0.82, 1.0),
           "hand_r:F>D": 18, "spine_03:F>R": 1, "head:F>R": 3}),
    (0.85, {"upperarm_r:aim": (0.35, -0.85, 0.35, 0.35), "lowerarm_r:aim": (0.15, -0.55, 0.82, 0.35), "hand_r:F>D": 6}),
    (1.0, {}),
])

LOOK_LEFT = dict(name="look_left", base="seated", mode="additive", fps=30.0, length=0.6, keys=[
    (0.0, {}),
    (0.3, {"spine_02:F>L": 4, "spine_03:F>L": 6, "neck_01:F>L": 14, "head:F>L": 22, "head:U>L": 2}),
    (0.6, {"spine_02:F>L": 8, "spine_03:F>L": 11, "neck_01:F>L": 24, "head:F>L": 36, "head:U>L": 3, "clavicle_l:F>B": 3}),
])
LOOK_RIGHT = mirror(LOOK_LEFT, "look_right")

# hard braking: torso pitched toward the wheel, shoulders forward and braced, head held up against the pull
BRAKE_BRACE = dict(name="brake_brace", base="seated", mode="additive", fps=30.0, length=0.5, keys=[
    (0.0, {}),
    (0.5, {"spine_01:U>F": 2, "spine_02:U>F": 3, "spine_03:U>F": 2, "neck_01:F>U": 4, "head:F>U": 3,
           "clavicle_l:B>F": 4, "clavicle_r:B>F": 4, "clavicle_l:L>U": 1.5, "clavicle_r:R>U": 1.5}),
])

# a hit: the body is thrown forward and to the side, the head whips and settles (one-shot, back to zero)
CRASH_JOLT = dict(name="crash_jolt", base="seated", mode="additive", fps=30.0, length=0.8, keys=[
    (0.0, {}),
    (0.07, {"spine_02:U>F": 6, "spine_03:U>F": 6, "spine_03:U>R": 3, "neck_01:U>B": 4, "head:U>F": 10,
            "clavicle_l:L>U": 4, "clavicle_r:R>U": 4}),
    (0.18, {"spine_02:U>F": 2, "spine_03:U>F": 1, "spine_03:U>L": 1.5, "neck_01:U>F": 2, "head:U>B": 8, "head:U>L": 3,
            "clavicle_l:L>U": 2, "clavicle_r:R>U": 2}),
    (0.34, {"spine_03:U>F": 1, "head:U>F": 3, "head:U>R": 1}),
    (0.8, {}),
])

# race won / finished: the right hand leaves the wheel for a fist pump over the head (the left stays on the wheel)
CELEBRATE = dict(name="celebrate", base="seated", mode="pose", fps=30.0, length=2.4, keys=[
    (0.0, {}),
    (0.3, {"upperarm_r:aim": (0.35, 0.8, 0.35, 1.0), "lowerarm_r:aim": (0.05, 1.0, 0.1, 1.0), "hand_r:F>U": 10,
           "head:F>U": 6, "spine_03:U>L": 2, "spine_03:F>U": 2}),
    (0.55, {"upperarm_r:aim": (0.3, 0.9, 0.25, 1.0), "lowerarm_r:aim": (0.0, 1.0, 0.0, 1.0), "hand_r:F>U": 4,
            "head:F>U": 9, "spine_03:U>L": 3, "spine_03:F>U": 3}),
    (0.8, {"upperarm_r:aim": (0.35, 0.75, 0.4, 1.0), "lowerarm_r:aim": (0.1, 0.9, 0.35, 1.0), "hand_r:F>U": 12,
           "head:F>U": 6, "spine_03:U>L": 2, "spine_03:F>U": 2}),
    (1.05, {"upperarm_r:aim": (0.3, 0.9, 0.25, 1.0), "lowerarm_r:aim": (0.0, 1.0, 0.0, 1.0), "hand_r:F>U": 4,
            "head:F>U": 10, "spine_03:U>L": 3, "spine_03:F>U": 3}),
    (1.3, {"upperarm_r:aim": (0.35, 0.75, 0.4, 1.0), "lowerarm_r:aim": (0.1, 0.9, 0.35, 1.0), "hand_r:F>U": 12,
           "head:F>U": 6, "spine_03:U>L": 2, "spine_03:F>U": 2}),
    (1.55, {"upperarm_r:aim": (0.3, 0.9, 0.25, 1.0), "lowerarm_r:aim": (0.0, 1.0, 0.0, 1.0), "hand_r:F>U": 4,
            "head:F>U": 10, "spine_03:U>L": 3, "spine_03:F>U": 3}),
    (1.95, {"upperarm_r:aim": (0.35, -0.2, 0.7, 0.5), "lowerarm_r:aim": (0.1, 0.2, 0.9, 0.5), "head:F>U": 3}),
    (2.4, {}),
])

IDLE_STANDING = dict(name="idle_standing", base="standing", mode="full", fps=15.0, length=4.0, loop=True, keys=[
    (0.0, _arms_down({"spine_02:F>U": 0.0})),
    (1.0, _arms_down({"spine_02:F>U": 0.8, "spine_03:F>U": 0.8, "clavicle_l:L>U": 1.0, "clavicle_r:R>U": 1.0,
                      "pelvis:pos": (0.01, 0.0, 0.0), "pelvis:U>R": 1.0, "head:F>L": 2})),
    (2.0, _arms_down({"spine_02:F>U": 1.2, "spine_03:F>U": 1.2, "clavicle_l:L>U": 1.5, "clavicle_r:R>U": 1.5,
                      "pelvis:pos": (0.015, 0.0, 0.0), "pelvis:U>R": 1.5})),
    (3.0, _arms_down({"spine_02:F>U": 0.5, "spine_03:F>U": 0.5, "clavicle_l:L>U": 0.6, "clavicle_r:R>U": 0.6,
                      "pelvis:pos": (0.005, 0.0, 0.0), "pelvis:U>R": 0.6, "head:F>R": 2})),
])


def _walk_key(phase):
    """one walk pose at phase 0..1 (0 = left heel strike)."""
    s = math.sin(2 * math.pi * phase); c = math.cos(2 * math.pi * phase)
    swing_l = 24 * c; swing_r = -24 * c                       # thigh: + = leg forward
    knee_l = 38 * max(0.0, math.sin(2 * math.pi * (phase - 0.55))) + 6
    knee_r = 38 * max(0.0, math.sin(2 * math.pi * (phase - 0.05))) + 6
    k = _arms_down({
        "thigh_l:D>F": swing_l, "thigh_r:D>F": swing_r,
        "calf_l:D>B": knee_l, "calf_r:D>B": knee_r,
        "foot_l:F>U": 8 * max(0.0, c), "foot_r:F>U": 8 * max(0.0, -c),
        "upperarm_l:D>F": -16 * c, "upperarm_r:D>F": 16 * c,
        "lowerarm_l:D>F": 10 + 6 * max(0.0, -c), "lowerarm_r:D>F": 10 + 6 * max(0.0, c),
        "spine_02:F>L": -3 * c, "pelvis:F>L": 4 * c, "spine_01:U>F": 3,
        "pelvis:pos": (0.0, 0.015 * abs(math.cos(2 * math.pi * phase)) - 0.012, 0.0),
    })
    return k


WALK = dict(name="walk", base="standing", mode="full", fps=30.0, length=1.0, loop=True,
            keys=[(p / 8.0, _walk_key(p / 8.0)) for p in range(8)])

WAVE = dict(name="wave", base="standing", mode="full", fps=30.0, length=2.4, keys=[
    (0.0, _arms_down()),
    (0.35, _arms_down({"upperarm_r:aim": (0.75, 0.55, 0.25, 1.0), "lowerarm_r:aim": (0.1, 1.0, 0.15, 1.0),
                       "hand_r:F>U": 5, "head:F>R": 6})),
    (0.6, _arms_down({"upperarm_r:aim": (0.75, 0.55, 0.25, 1.0), "lowerarm_r:aim": (0.1, 1.0, 0.15, 1.0),
                      "lowerarm_r:U>R": 20, "hand_r:U>R": 10, "head:F>R": 6})),
    (0.85, _arms_down({"upperarm_r:aim": (0.75, 0.55, 0.25, 1.0), "lowerarm_r:aim": (0.1, 1.0, 0.15, 1.0),
                       "lowerarm_r:U>L": 18, "hand_r:U>L": 10, "head:F>R": 6})),
    (1.1, _arms_down({"upperarm_r:aim": (0.75, 0.55, 0.25, 1.0), "lowerarm_r:aim": (0.1, 1.0, 0.15, 1.0),
                      "lowerarm_r:U>R": 20, "hand_r:U>R": 10, "head:F>R": 6})),
    (1.35, _arms_down({"upperarm_r:aim": (0.75, 0.55, 0.25, 1.0), "lowerarm_r:aim": (0.1, 1.0, 0.15, 1.0),
                       "lowerarm_r:U>L": 18, "hand_r:U>L": 10, "head:F>R": 6})),
    (1.6, _arms_down({"upperarm_r:aim": (0.75, 0.55, 0.25, 1.0), "lowerarm_r:aim": (0.1, 1.0, 0.15, 1.0),
                      "head:F>R": 4})),
    (2.4, _arms_down()),
])

CLIPS = [IDLE_SEATED, STEER_LEFT, STEER_RIGHT, SHIFT, LOOK_LEFT, LOOK_RIGHT, BRAKE_BRACE, CRASH_JOLT, CELEBRATE,
         IDLE_STANDING, WALK, WAVE]
MODE_FLAGS = {"additive": 2, "pose": 0, "full": 4}   # .dra flags (1 loop is added for loops)


# ================================================================================================ riding a sport bike
# A base pose like seated_base (not an Unreal clip): solved here in Unity space and written into driver_anims.dra as
# "ride_sportbike" (flags 4: every bone's local rotation + the pelvis position), by `python anim_clips.py --ride` or
# by build_driver.py. The Driver plugin starts from it on a Bikes motorcycle and re-solves the same steps as
# ride_fit / ride_frame below (Solver.FitBike / FrameBike): hips onto the seat, feet onto the pegs with the knees
# against the tank, hands onto the grips, hang-off in corners. Unity space: x right, y up, z forward, metres.

# Bikes plugin sockets in the bike frame ("Bikes.Lean": origin on the ground midway between the axles, +z forward,
# +y up); the right side is given, the left is mirrored. hip = the hip joints (thigh heads) above the seat point,
# knee = knee half-width (knees against the tank).
BIKES = {
    "S1000RR": dict(seat=(0.0, 0.82, -0.18), grip=(0.32, 0.86, 0.38), peg=(0.17, 0.36, -0.30), hip=0.10, knee=0.20),
    "SportBike": dict(seat=(0.0, 0.85, -0.20), grip=(0.33, 0.90, 0.40), peg=(0.18, 0.38, -0.32), hip=0.10, knee=0.20),
}

# the torso of the pose preset (fixed in the clip; the plugin only moves the pelvis onto the seat)
RIDE_SPORTBIKE = dict(name="ride_sportbike", bike="S1000RR",
                      pelvis_tilt=26.0,   # deg, pelvis rolled forward over the seat
                      torso=52.0,         # deg from vertical, spine_01 -> neck_01 (spread 40/30/30 over spine_01..03)
                      neck=24.0,          # deg the neck stays pitched forward of upright (world)
                      head=6.0,           # deg the head looks down from level (chin over the tank, eyes up the road)
                      clav_fwd=8.0, clav_up=2.0)   # deg, shoulders reach forward to the clip-ons

# the solve (the same numbers are in the plugin's Solver.cs, "bike" constants)
RIDE = dict(arm_pole=(0.55, -0.30, -0.15),   # elbow pole from the shoulder (x outward): elbows bent and out
            hand_tilt=12.0,                  # deg the hand's distal axis points down from level
            ball_up=0.035,                   # ball joint above the peg (the sole on the peg)
            toe_dir=(0.10, -0.70, 1.0),      # ankle -> ball (x outward): toes forward and out, heel up
            knee_fwd=(0.0, 0.45, 1.0),       # the knee's starting side (forward and up) before it is swung to the tank
            hang_hip=0.15,                   # m the hips slide to the inside at full hang-off
            hang_full=30.0,                  # deg lean at which the hip slide is complete
            hang_roll=6.0,                   # deg the pelvis rolls to the inside with the slide
            hang_spine=3.0,                  # deg the upper body leans to the inside with the slide
            hang_body=12.0,                  # deg the upper body adds to the inside past hang_full ...
            hang_body_span=15.0,             # ... reached hang_body over this many more degrees
            hang_knee=0.12,                  # m the inside knee opens out
            head_level=0.3)                  # share of the lean the head rolls back toward the horizon


class RidePose:
    """A pose in Unity space with world FK and the model's sockets (name -> (bone, pos, rot))."""

    def __init__(s, sk, socks, lp, lq):
        s.sk = sk; s.socks = socks; s.lp = list(lp); s.lq = list(lq)
        s.rest_wp, s.rest_wq = sk.fk(sk.rest_lp, sk.rest_lq)
        s.fk()

    def i(s, n): return s.sk.idx[n]
    def fk(s): s.wp, s.wq = s.sk.fk(s.lp, s.lq)

    def set_world(s, b, q):
        p = s.sk.par[b]
        s.lq[b] = qnorm(qmul(qinv(s.wq[p]), q)) if p >= 0 else qnorm(q)
        s.fk()

    def rot_world(s, b, q): s.set_world(b, qmul(q, s.wq[b]))

    def sock(s, name):
        b, p, _ = s.socks[name]
        return vadd(s.wp[b], qrot(s.wq[b], p))

    def move_pelvis(s, d):
        b = s.i("pelvis"); p = s.sk.par[b]
        s.lp[b] = vadd(s.lp[b], qrot(qinv(s.wq[p]), d) if p >= 0 else d)
        s.fk()


def _vlen(a): return math.sqrt(vdot(a, a))


def ik2(R, b1, b2, b3, target, pole):
    """Two-bone IK (Solver.Ik2): b1 aims at the elbow / knee, rolled so its hinge (local X) is normal to the IK plane,
    then b2 aims at the end. Returns how far the end is short of the target."""
    A_, B_, C_ = R.wp[b1], R.wp[b2], R.wp[b3]
    l1, l2 = _vlen(vsub(B_, A_)), _vlen(vsub(C_, B_))
    d = vsub(target, A_); dist = max(1e-4, min(_vlen(d), (l1 + l2) * 0.999)); dn = vnorm(d)
    pv = vsub(pole, A_); pn = vnorm(vsub(pv, vmul(dn, vdot(pv, dn))))
    ca = max(-1.0, min(1.0, (l1 * l1 + dist * dist - l2 * l2) / (2 * l1 * dist))); an = math.acos(ca)
    elbow = vadd(vadd(A_, vmul(dn, ca * l1)), vmul(pn, math.sin(an) * l1)); end = vadd(A_, vmul(dn, dist))
    R.rot_world(b1, qfromto(vsub(B_, A_), vsub(elbow, A_)))
    ax = vnorm(vsub(R.wp[b2], A_)); nrm = vcross(vsub(elbow, A_), vsub(end, elbow))
    if _vlen(nrm) > 1e-6:
        nrm = vnorm(nrm); X = qrot(R.wq[b1], (1, 0, 0))
        if vdot(X, nrm) < 0: nrm = vmul(nrm, -1)
        Xp = vnorm(vsub(X, vmul(ax, vdot(X, ax))))
        a2 = math.acos(max(-1.0, min(1.0, vdot(Xp, nrm))))
        if vdot(vcross(Xp, nrm), ax) < 0: a2 = -a2
        R.rot_world(b1, qaxis(ax, math.degrees(a2)))
    B_, C_ = R.wp[b2], R.wp[b3]
    R.rot_world(b2, qfromto(vsub(C_, B_), vsub(end, B_)))
    return _vlen(vsub(target, end))


def bike_side(bike, key, sg):
    p = bike[key]; return (sg * p[0], p[1], p[2])


def ride_torso(R, P=RIDE_SPORTBIKE):
    """The preset's torso on the rest pose: pelvis tilt, spine pitch, neck and head, shoulders (no IK)."""
    X = (1.0, 0.0, 0.0)
    pel = R.i("pelvis"); s1, s2, s3 = R.i("spine_01"), R.i("spine_02"), R.i("spine_03"); nk, hd = R.i("neck_01"), R.i("head")
    R.rot_world(pel, qaxis(X, P["pelvis_tilt"]))
    for _ in range(8):   # spine_01 -> neck_01 pitched P["torso"] from vertical, spread 40/30/30
        v = vsub(R.wp[nk], R.wp[s1]); cur = math.degrees(math.atan2(v[2], v[1]))
        e = P["torso"] - cur
        if abs(e) < 1e-3: break
        for b, f in ((s1, 0.4), (s2, 0.3), (s3, 0.3)): R.rot_world(b, qaxis(X, e * f))
    R.set_world(nk, qmul(qaxis(X, P["neck"]), R.rest_wq[nk]))
    R.set_world(hd, qmul(qaxis(X, P["head"]), R.rest_wq[hd]))
    for sf, sg in (("_l", -1.0), ("_r", 1.0)):
        c = R.i("clavicle" + sf)
        R.rot_world(c, qaxis(vcross((sg, 0, 0), (0, 0, 1)), P["clav_fwd"]))   # shoulder toward the front
        R.rot_world(c, qaxis(vcross((sg, 0, 0), (0, 1, 0)), P["clav_up"]))    # and a little up


def ride_place(R, bike, hip_shift=(0.0, 0.0, 0.0)):
    """The hip joints (midpoint of the thigh heads) onto the seat point + bike["hip"] up (+ the hang-off shift)."""
    t = vadd(vadd(bike["seat"], (0.0, bike["hip"], 0.0)), hip_shift)
    m = vmul(vadd(R.wp[R.i("thigh_l")], R.wp[R.i("thigh_r")]), 0.5)
    R.move_pelvis(vsub(t, m))


def knee_point(hip, ank, l1, l2, sg, width, fwd):
    """Where the knee goes: on the IK circle of (hip, ankle), swung from the forward-up side until its x is sg*width."""
    d = vsub(ank, hip); dist = max(1e-4, min(_vlen(d), (l1 + l2) * 0.999)); dn = vnorm(d)
    ca = max(-1.0, min(1.0, (l1 * l1 + dist * dist - l2 * l2) / (2 * l1 * dist)))
    c = vadd(hip, vmul(dn, ca * l1)); r = math.sqrt(max(0.0, 1 - ca * ca)) * l1
    u0 = vnorm(vsub(fwd, vmul(dn, vdot(fwd, dn))))
    out = vnorm(vcross(dn, u0)); out = out if out[0] * sg > 0 else vmul(out, -1)
    lo, hi = -0.6, 1.3
    for _ in range(24):
        ph = 0.5 * (lo + hi)
        k = vadd(c, vadd(vmul(u0, r * math.cos(ph)), vmul(out, r * math.sin(ph))))
        if k[0] * sg < width: lo = ph
        else: hi = ph
    ph = 0.5 * (lo + hi)
    return vadd(c, vadd(vmul(u0, r * math.cos(ph)), vmul(out, r * math.sin(ph))))


def ride_legs(R, bike, knee_out=(0.0, 0.0)):
    """Balls of the feet on the pegs, knees in against the tank (knee_out: extra width per side, hang-off)."""
    res = {}
    for k, (sf, sg) in enumerate((("_l", -1.0), ("_r", 1.0))):
        th, ca, ft, bl = R.i("thigh" + sf), R.i("calf" + sf), R.i("foot" + sf), R.i("ball" + sf)
        foot_len = _vlen(vsub(R.rest_wp[bl], R.rest_wp[ft]))
        td = vnorm((sg * RIDE["toe_dir"][0], RIDE["toe_dir"][1], RIDE["toe_dir"][2]))
        ball = vadd(bike_side(bike, "peg", sg), (0.0, RIDE["ball_up"], 0.0))
        ank = vsub(ball, vmul(td, foot_len))
        l1 = _vlen(vsub(R.wp[ca], R.wp[th])); l2 = _vlen(vsub(R.wp[ft], R.wp[ca]))
        knee = knee_point(R.wp[th], ank, l1, l2, sg, bike["knee"] + knee_out[k], RIDE["knee_fwd"])
        short = ik2(R, th, ca, ft, ank, knee)
        rd = vnorm(vsub(R.rest_wp[bl], R.rest_wp[ft]))
        R.set_world(ft, qmul(qfromto(rd, td), R.rest_wq[ft]))
        res["leg" + sf] = (round(short * 100, 2), tuple(round(x, 3) for x in R.wp[ca]))
    return res


def grip_frame(sg, bars_q=(0.0, 0.0, 0.0, 1.0)):
    """The hand's grip-socket frame on the bar: +Z pinky -> index along the bar toward the tank, +Y the palm normal
    (down onto the bar, square to fingers that point forward and hand_tilt down), X = Y x Z (the socket frame is
    mirrored between the hands, so X is the distal axis on the left and its opposite on the right); turned with the
    bars."""
    t = math.radians(RIDE["hand_tilt"])
    Y = (0.0, -math.cos(t), -math.sin(t)); Z = (-sg, 0.0, 0.0); X = vcross(Y, Z)
    return qrot(bars_q, X), qrot(bars_q, Y), qrot(bars_q, Z)


def _from_axes(X, Y, Z):
    m00, m10, m20, m01, m11, m21, m02, m12, m22 = X[0], X[1], X[2], Y[0], Y[1], Y[2], Z[0], Z[1], Z[2]
    tr = m00 + m11 + m22
    if tr > 0:
        s = math.sqrt(tr + 1) * 2; q = ((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s)
    elif m00 > m11 and m00 > m22:
        s = math.sqrt(1 + m00 - m11 - m22) * 2; q = (0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s)
    elif m11 > m22:
        s = math.sqrt(1 + m11 - m00 - m22) * 2; q = ((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s)
    else:
        s = math.sqrt(1 + m22 - m00 - m11) * 2; q = ((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s)
    return qnorm(q)


def ride_arms(R, bike, bars_q=(0.0, 0.0, 0.0, 1.0), bars_pivot=None):
    """Hands onto the grips (two-bone IK, elbows bent and out, half the wrist twist on lowerarm_twist_01)."""
    res = {}
    for sf, sg in (("_l", -1.0), ("_r", 1.0)):
        up, lo, tw, hd = R.i("upperarm" + sf), R.i("lowerarm" + sf), R.i("lowerarm_twist_01" + sf), R.i("hand" + sf)
        g = bike_side(bike, "grip", sg)
        if bars_pivot is not None: g = vadd(bars_pivot, qrot(bars_q, vsub(g, bars_pivot)))
        X, Y, Z = grip_frame(sg, bars_q)
        gb, gp, gq = R.socks["grip" + sf]
        H = qnorm(qmul(_from_axes(X, Y, Z), qinv(gq)))
        wrist = vsub(g, qrot(H, gp))
        # an arm that can't reach is lengthened up to 12% (as the plugin's Reach with stretch)
        rl = R.sk.rest_lp; reach = _vlen(rl[lo]) + _vlen(rl[hd])
        k = max(1.0, min(1.12, _vlen(vsub(wrist, R.wp[up])) / (0.999 * reach)))
        R.lp[lo] = vmul(rl[lo], k); R.lp[hd] = vmul(rl[hd], k); R.lp[tw] = vmul(rl[tw], k); R.fk()
        pole = vadd(R.wp[up], (sg * RIDE["arm_pole"][0], RIDE["arm_pole"][1], RIDE["arm_pole"][2]))
        short = ik2(R, up, lo, hd, wrist, pole)
        R.set_world(hd, H)
        # half the forearm twist onto lowerarm_twist_01 (bone axis = local z)
        D = qmul(qmul(qinv(R.wq[lo]), H), qinv(R.sk.rest_lq[hd]))
        t = 2 * math.atan2(D[2], D[3])
        if t > math.pi: t -= 2 * math.pi
        if t < -math.pi: t += 2 * math.pi
        R.lq[tw] = qnorm(qmul(qaxis((0, 0, 1), math.degrees(0.5 * t)), R.sk.rest_lq[tw])); R.fk()
        el = R.wp[lo]
        res["arm" + sf] = (round(short * 100, 2), tuple(round(x, 3) for x in el), round(k, 3))
    return res


def hang_amounts(lean, on=True):
    """(hip slide 0-1, extra upper-body degrees, side: +1 = right) for a bike lean in degrees (+ = leaning left)."""
    if not on or abs(lean) < 1e-3: return 0.0, 0.0, 0.0
    def sm(x): x = max(0.0, min(1.0, x)); return x * x * (3 - 2 * x)
    a = abs(lean)
    return sm(a / RIDE["hang_full"]), RIDE["hang_body"] * sm((a - RIDE["hang_full"]) / RIDE["hang_body_span"]), (-1.0 if lean > 0 else 1.0)


def ride_fit(sk, socks, base_lp, base_lq, bike):
    """Solver.FitBike: the clip's pose, hips onto the seat, legs onto the pegs. Arms are solved per frame."""
    R = RidePose(sk, socks, base_lp, base_lq)
    ride_place(R, bike)
    res = ride_legs(R, bike)
    return R, res


def ride_frame(R, bike, lean=0.0, hang_on=True, bars_q=(0.0, 0.0, 0.0, 1.0), bars_pivot=None):
    """Solver.FrameBike (without the clip layers): hang-off, head toward the horizon, legs, arms. R is changed."""
    h, body, side = hang_amounts(lean, hang_on)
    res = {"hang": (round(h, 2), round(body, 1), side)}
    pel, s1, hd = R.i("pelvis"), R.i("spine_01"), R.i("head")
    if h > 0:
        R.move_pelvis((side * RIDE["hang_hip"] * h, -0.02 * h, 0.0))
        R.rot_world(pel, qaxis((0, 0, 1), -side * RIDE["hang_roll"] * h))
        R.rot_world(s1, qaxis((0, 0, 1), -side * (body + RIDE["hang_spine"] * h)))
        knee = (RIDE["hang_knee"] * h if side < 0 else 0.0, RIDE["hang_knee"] * h if side > 0 else 0.0)
        res.update(ride_legs(R, bike, knee))
    if abs(lean) > 1e-3: R.rot_world(hd, qaxis((0, 0, 1), -RIDE["head_level"] * lean))
    res.update(ride_arms(R, bike, bars_q, bars_pivot))
    return res


def ride_clip(sk, socks, P=RIDE_SPORTBIKE):
    """The ride_sportbike base clip (dra dict, flags 4) and a fit report: torso on rest, fitted to P's bike."""
    R = RidePose(sk, socks, sk.rest_lp, sk.rest_lq)
    ride_torso(R, P)
    bike = BIKES[P["bike"]]
    ride_place(R, bike)
    rep = ride_legs(R, bike); rep.update(ride_arms(R, bike))
    eye = R.sock("eye_c"); rep["eye"] = tuple(round(x, 3) for x in eye)
    rep["chin_z"] = round(R.wp[R.i("head")][2], 3)
    v = vsub(R.wp[R.i("neck_01")], R.wp[R.i("spine_01")]); rep["torso_deg"] = round(math.degrees(math.atan2(v[2], v[1])), 1)
    tracks = [(n, 3 if n == "pelvis" else 1) for n in sk.names]
    frame = [(R.lq[i], tuple(R.lp[i]) if n == "pelvis" else None) for i, n in enumerate(sk.names)]
    return dict(name=P["name"], fps=30.0, flags=4, tracks=tracks, frames=[frame]), R, rep


def socks_of(drm):
    return {n: (b, tuple(p), qnorm(tuple(q))) for n, b, p, q in drm["sockets"]}


def write_ride(drm_path, dra_path):
    """ride_sportbike into dra_path: replaced if there, else added after breathe_add; every other clip kept as is."""
    import drm_io
    d = drm_io.read_drm(drm_path)
    sk = Skel([b[0] for b in d["skel"]], [b[1] for b in d["skel"]], [b[2] for b in d["skel"]], [b[3] for b in d["skel"]])
    clip, R, rep = ride_clip(sk, socks_of(d))
    clips = drm_io.read_dra(dra_path)
    names = [c["name"] for c in clips]
    if clip["name"] in names: clips[names.index(clip["name"])] = clip
    else: clips.insert(names.index("breathe_add") + 1 if "breathe_add" in names else len(clips), clip)
    n = drm_io.write_dra(dra_path, clips)
    return rep, [c["name"] for c in clips], n


# ------------------------------------------------------------------------------------------------ self-check (Unity space)
def unity_skel(drm_path, dra_path=None):
    import drm_io
    d = drm_io.read_drm(drm_path)
    sk = Skel([b[0] for b in d["skel"]], [b[1] for b in d["skel"]], [b[2] for b in d["skel"]], [b[3] for b in d["skel"]])
    base = None
    if dra_path:
        for c in drm_io.read_dra(dra_path):
            if c["name"] == "seated_base":
                lp, lq = list(sk.rest_lp), list(sk.rest_lq)
                for (b, ch), (q, p) in zip(c["tracks"], c["frames"][0]):
                    i = sk.idx[b]
                    if ch & 1: lq[i] = qnorm(q)
                    if ch & 2: lp[i] = tuple(p)
                base = (lp, lq)
    return sk, base


if __name__ == "__main__":
    import os, sys
    here = os.path.dirname(os.path.abspath(__file__))
    if "--ride" in sys.argv:   # python anim_clips.py --ride [driver.drm [driver_anims.dra]]
        a = [x for x in sys.argv[1:] if x != "--ride"]
        drm = a[0] if a else os.path.join(here, "driver.drm")
        dra = a[1] if len(a) > 1 else os.path.join(os.path.dirname(drm), "driver_anims.dra")
        rep, names, n = write_ride(drm, dra)
        for k in sorted(rep): print("  RIDE %-8s %s" % (k, rep[k]))
        bad = [k for k in rep if k.startswith(("arm", "leg")) and rep[k][0] > 1.0]
        print("  wrote %s: %d clips (%s), %d bytes" % (dra, len(names), ", ".join(names), n))
        print("RESULT: " + ("OK" if not bad else "FAIL (out of reach: %s)" % ", ".join(bad)))
        sys.exit(0 if not bad else 1)
    drm = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "driver.drm")
    sk, seated = unity_skel(drm, os.path.join(os.path.dirname(drm), "driver_anims.dra"))
    print("axes R %s U %s F %s" % tuple(tuple(round(x, 3) for x in a) for a in sk.axes))
    ok = True
    for c in CLIPS:
        blp, blq = seated if c["base"] == "seated" else (sk.rest_lp, sk.rest_lq)
        n = frames(c); worst = 0.0
        for f in range(n):
            lp, lq = pose(c, f / c["fps"], sk, blp, blq)
            for q in lq:
                if abs(sum(x * x for x in q) - 1) > 1e-4: ok = False
            for i in range(len(lq)):
                d = qmul(qinv(blq[i]), lq[i]); worst = max(worst, math.degrees(2 * math.acos(min(1.0, abs(d[3])))))
        print("  %-14s %-9s %-8s %3d frames, %2d channels, largest bone turn %5.1f deg" %
              (c["name"], c["base"], c["mode"], n, len(channels(c)), worst))
    print("RESULT: " + ("OK" if ok else "FAIL"))
