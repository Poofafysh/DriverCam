"""Driver clips: Unreal FBX exports -> driver_anims.dra (the plugin's clip file). Blender headless:

    blender -b --factory-startup --python fbx_to_dra.py -- <fbxdir> [--out driver_anims.dra] [--drm driver.drm]
            [--keep driver_anims.dra]

<fbxdir> holds A_<clip>.fbx and clips.json, written by ue_anims.py (Unreal Engine 5.8). The motion itself is defined
in anim_clips.py (original keyframes on our own skeleton); this script only carries it from Unreal to Unity space:

1. imports each FBX; per bone and key, delta world rotation D = W_pose * W_rest^-1 (independent of how the importer
   orients bones) and the pelvis joint position;
2. maps Blender world to Unity world with a Kabsch fit (scale + orthogonal, reflection allowed) of the FBX rest joints
   onto driver.drm's rest joints: D_u = M D M^T, then Unity world = D_u * rest world, local = parent^-1 * world;
3. writes per mode (clips.json): additive = bone-local deltas against seated_base (flags 2), pose = full local
   rotations of the bones that move (flags 0), full = every bone's local rotation + pelvis position (flags 4); loops
   add flag 1 and leave out the closing key (it equals key 0);
4. keeps seated_base, breathe_add and (when there) ride_sportbike from --keep (default: the current driver_anims.dra) at
   the front;
5. round-trip check: every clip against anim_clips.pose() evaluated directly in Unity space; prints the largest bone
   error (degrees) per clip and ends with RESULT: OK (all < 0.5 deg, Kabsch residual < 1 mm) or FAIL.
"""
import bpy, json, math, os, sys
import numpy as np
from mathutils import Matrix, Quaternion

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import drm_io, anim_clips as A   # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
def opt(name, default):
    return argv[argv.index(name) + 1] if name in argv else default
FBX_DIR = argv[0]
DRM = opt("--drm", os.path.join(HERE, "driver.drm"))
OUT = opt("--out", os.path.join(HERE, "driver_anims.dra"))
KEEP = opt("--keep", os.path.join(HERE, "driver_anims.dra"))
TOL_DEG, TOL_FIT = 0.5, 0.001


def q_np(q):   # xyzw tuple -> mathutils (wxyz)
    return Quaternion((q[3], q[0], q[1], q[2]))


def q_t(q):    # mathutils -> xyzw tuple (w >= 0 not forced: neighbours stay continuous below)
    return (q.x, q.y, q.z, q.w)


def ang(a, b):
    d = A.qmul(A.qinv(a), b)
    return math.degrees(2 * math.acos(min(1.0, abs(d[3]))))


# ---------------------------------------------------------------------------------------------- Unity skeleton + bases
d = drm_io.read_drm(DRM)
names = [b[0] for b in d["skel"]]; parents = [b[1] for b in d["skel"]]
sk = A.Skel(names, parents, [b[2] for b in d["skel"]], [b[3] for b in d["skel"]])
rest_wp, rest_wq = sk.fk(sk.rest_lp, sk.rest_lq)
kept = [c for c in drm_io.read_dra(KEEP) if c["name"] in ("seated_base", "breathe_add", "ride_sportbike")]
if [c["name"] for c in kept][:2] != ["seated_base", "breathe_add"]:
    raise SystemExit("RESULT: FAIL %s has no seated_base + breathe_add" % KEEP)
seat_lp, seat_lq = list(sk.rest_lp), list(sk.rest_lq)
for (b, ch), (q, p) in zip(kept[0]["tracks"], kept[0]["frames"][0]):
    if ch & 1: seat_lq[sk.idx[b]] = A.qnorm(q)
    if ch & 2: seat_lp[sk.idx[b]] = tuple(p)


def kabsch(src, dst):
    """dst ~ s * M @ src + t, M orthogonal (det +-1)."""
    S = np.array(src); D = np.array(dst); cs = S.mean(0); cd = D.mean(0); S0 = S - cs; D0 = D - cd
    s = math.sqrt((D0 ** 2).sum() / (S0 ** 2).sum())
    U, _, Vt = np.linalg.svd(S0.T @ D0)
    M = (U @ Vt).T
    t = cd - s * M @ cs
    res = np.sqrt((((s * (M @ S.T)).T + t - D) ** 2).sum(1)).max()
    return s, M, t, res


def read_fbx(path, entry):
    """per key: {bone: (delta world rotation 3x3 in Blender world, joint world position)}; plus the rest joints."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path)
    arm = [o for o in bpy.data.objects if o.type == "ARMATURE"][0]
    sc = bpy.context.scene; fps = sc.render.fps / sc.render.fps_base
    act = arm.animation_data.action
    start = act.frame_range[0]
    Wa = arm.matrix_world
    def rot3(m):
        r = m.to_3x3(); return Matrix([c.normalized() for c in r.col]).transposed()   # columns normalised
    rest = {}
    for b in arm.data.bones:
        m = Wa @ b.matrix_local
        rest[b.name] = (rot3(m), m.translation.copy())
    nkeys = entry["frames"]                    # a loop's closing key is left out
    keys = []
    for k in range(nkeys):
        f = start + k / entry["fps"] * fps
        sc.frame_set(int(math.floor(f)), subframe=f - math.floor(f))
        fr = {}
        for pb in arm.pose.bones:
            m = Wa @ pb.matrix
            fr[pb.name] = (rot3(m) @ rest[pb.name][0].transposed(), m.translation.copy())
        keys.append(fr)
    return rest, keys


def to_unity(rest, keys):
    common = [n for n in names if n in rest]
    s, M, t, res = kabsch([tuple(rest[n][1]) for n in common], [rest_wp[sk.idx[n]] for n in common])
    Mm = Matrix(M.tolist())
    poses = []
    for fr in keys:
        wq = [None] * len(names); lq = [None] * len(names); lp = list(sk.rest_lp)
        for i, n in enumerate(names):
            if n in fr:
                Du = (Mm @ fr[n][0] @ Mm.transposed()).to_quaternion()
                wq[i] = A.qnorm(A.qmul(q_t(Du), rest_wq[i]))
            else:
                wq[i] = rest_wq[i] if parents[i] < 0 else A.qnorm(A.qmul(wq[parents[i]], sk.rest_lq[i]))
            p = parents[i]
            lq[i] = A.qnorm(A.qmul(A.qinv(wq[p]), wq[i])) if p >= 0 else wq[i]
            if n == "pelvis" and n in fr:
                pu = tuple(s * (M @ np.array(tuple(fr[n][1]))) + t)
                # parent (root) never moves, so its rest world frame is the frame for the local position
                lp[i] = A.qrot(A.qinv(rest_wq[p]), A.vsub(pu, rest_wp[p])) if p >= 0 else pu
        poses.append((lp, lq))
    return poses, res


def continuous(qs):
    out = []
    for q in qs:
        if out and sum(a * b for a, b in zip(out[-1], q)) < 0: q = tuple(-c for c in q)
        out.append(q)
    return out


def build_clip(entry, poses):
    clip = next(c for c in A.CLIPS if c["name"] == entry["name"])
    mode = entry["mode"]; base_lp, base_lq = (seat_lp, seat_lq) if entry["base"] == "seated" else (sk.rest_lp, sk.rest_lq)
    flags = A.MODE_FLAGS[mode] | (1 if entry["loop"] else 0)
    if mode == "additive":
        vals = [[A.qnorm(A.qmul(A.qinv(base_lq[i]), lq[i])) for i in range(len(names))] for lp, lq in poses]
        used = [i for i in range(len(names)) if any(ang((0, 0, 0, 1), v[i]) > 0.01 for v in vals)]
        tracks = [(names[i], 1) for i in used]
        series = {i: continuous([v[i] for v in vals]) for i in used}
        frames = [[(series[i][k], None) for i in used] for k in range(len(poses))]
    elif mode == "pose":
        used = [i for i in range(len(names)) if any(ang(base_lq[i], lq[i]) > 0.01 for lp, lq in poses)]
        tracks = [(names[i], 1) for i in used]
        series = {i: continuous([lq[i] for lp, lq in poses]) for i in used}
        frames = [[(series[i][k], None) for i in used] for k in range(len(poses))]
    else:   # full: every bone's rotation, the pelvis also its position
        pel = sk.idx["pelvis"]
        tracks = [(n, 3 if i == pel else 1) for i, n in enumerate(names)]
        series = {i: continuous([lq[i] for lp, lq in poses]) for i in range(len(names))}
        frames = [[(series[i][k], tuple(poses[k][0][i]) if i == pel else None) for i in range(len(names))]
                  for k in range(len(poses))]
    # round trip against the definition, in Unity space
    worst = 0.0; worst_p = 0.0
    for k, (lp, lq) in enumerate(poses):
        elp, elq = A.pose(clip, k / clip["fps"], sk, base_lp, base_lq)
        for i in range(len(names)):
            worst = max(worst, ang(elq[i], lq[i]))
        worst_p = max(worst_p, math.sqrt(sum((a - b) ** 2 for a, b in zip(elp[sk.idx["pelvis"]], lp[sk.idx["pelvis"]]))))
    return dict(name=entry["name"], fps=float(entry["fps"]), flags=flags, tracks=tracks, frames=frames), worst, worst_p


def main():
    man = json.load(open(os.path.join(FBX_DIR, "clips.json")))
    want = [c["name"] for c in A.CLIPS]
    got = [c["name"] for c in man["clips"]]
    if got != want: raise SystemExit("RESULT: FAIL clips.json has %s, anim_clips has %s" % (got, want))
    clips = list(kept); ok = True
    for e in man["clips"]:
        if e["frames"] != A.frames(next(c for c in A.CLIPS if c["name"] == e["name"])):
            ok = False; print("FBX2DRA %s frame count differs from anim_clips" % e["name"])
        rest, keys = read_fbx(os.path.join(FBX_DIR, e["fbx"]), e)
        poses, fit = to_unity(rest, keys)
        c, worst, worst_p = build_clip(e, poses)
        bad = worst > TOL_DEG or fit > TOL_FIT or worst_p > TOL_FIT
        ok &= not bad
        print("FBX2DRA %-14s %-8s flags %d %3d frames %2d tracks  fit %.5f m  round trip %.3f deg %.4f m %s" %
              (c["name"], e["mode"], c["flags"], len(c["frames"]), len(c["tracks"]), fit, worst, worst_p,
               "FAIL" if bad else "ok"))
        clips.append(c)
    if not ok: raise SystemExit("RESULT: FAIL (nothing written)")
    n = drm_io.write_dra(OUT, clips)
    print("FBX2DRA wrote %s: %d clips, %d bytes" % (OUT, len(clips), n))
    print("RESULT: OK")


main()
