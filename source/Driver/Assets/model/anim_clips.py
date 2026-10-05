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
