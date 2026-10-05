"""Contact sheets of the Driver's clips as the plugin gets them: poses come from driver_anims.dra (not from the
Blender actions), so this checks the whole Unreal -> FBX -> .dra chain. Workbench renders, one PNG per clip.

    blender -b driver.blend --python preview_clips.py -- <outdir> [--dra driver_anims.dra] [clip names...]

Each sheet: 4 frames (first, 1/3, 2/3, last) from a 3/4 view on top and a front view below. Seated clips are shown on
seated_base with the seat / wheel props; additive clips are seated_base * delta with no IK (in game the plugin's arm IK
keeps the hands on the rim, so arm motion in steer_* is only a reference). Also prints per clip the lowest hand / foot
height and the largest elbow / knee bend, a quick broken-limb check (prints CHECK ... ok / SUSPECT).
"""
import bpy, math, os, sys
from math import radians
from mathutils import Matrix, Quaternion, Vector
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import drm_io, anim_clips as A   # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = argv[0]; os.makedirs(OUT, exist_ok=True)
DRA = argv[argv.index("--dra") + 1] if "--dra" in argv else os.path.join(HERE, "driver_anims.dra")
ONLY = [a for i, a in enumerate(argv[1:], 1) if not a.startswith("--") and argv[i - 1] != "--dra"]
C = Matrix(((-1, 0, 0), (0, 0, 1), (0, -1, 0)))          # Blender -> Unity
W, H = 360, 360

d = drm_io.read_drm(os.path.join(HERE, "driver.drm"))
names = [b[0] for b in d["skel"]]; parents = [b[1] for b in d["skel"]]
sk = A.Skel(names, parents, [b[2] for b in d["skel"]], [b[3] for b in d["skel"]])
rest_wp, rest_wq = sk.fk(sk.rest_lp, sk.rest_lq)
clips = {c["name"]: c for c in drm_io.read_dra(DRA)}


def apply_tracks(c, k, lp, lq, additive):
    lp = list(lp); lq = list(lq)
    for (b, ch), (q, p) in zip(c["tracks"], c["frames"][k]):
        i = sk.idx[b]
        if ch & 1: lq[i] = A.qnorm(A.qmul(lq[i], q)) if additive else A.qnorm(q)
        if ch & 2: lp[i] = tuple(p)
    return lp, lq


seat = apply_tracks(clips["seated_base"], 0, sk.rest_lp, sk.rest_lq, False)

sc = bpy.context.scene
arm = bpy.data.objects["Armature"]
if arm.animation_data: arm.animation_data.action = None
for pb in arm.pose.bones: pb.matrix_basis = Matrix.Identity(4)
bpy.context.view_layer.update()
for o in bpy.data.objects:
    if o.type == "MESH" and o.name.endswith("_LOD1"): o.hide_render = True
props = bpy.data.collections.get("Props")
Aw = arm.matrix_world
# rest check: Unity rest joints mapped back must land on the Blender rest heads
rest_err = max((Aw @ arm.data.bones[n].head_local - C.transposed() @ Vector(rest_wp[sk.idx[n]])).length
               for n in names if n in arm.data.bones)
print("CHECK rest joints Unity->Blender max error %.5f m %s" % (rest_err, "ok" if rest_err < 1e-3 else "SUSPECT"))


def set_pose(lp, lq):
    wp, wq = sk.fk(lp, lq)
    for i, n in enumerate(names):
        pb = arm.pose.bones.get(n)
        if pb is None: continue
        Du = Quaternion((wq[i][3], wq[i][0], wq[i][1], wq[i][2])) @ \
            Quaternion((rest_wq[i][3], rest_wq[i][0], rest_wq[i][1], rest_wq[i][2])).inverted()
        Db = C.transposed() @ Du.to_matrix() @ C
        R = Db @ arm.data.bones[n].matrix_local.to_3x3()
        M = R.to_4x4(); M.translation = Aw.inverted() @ (C.transposed() @ Vector(wp[i]))
        pb.matrix = M
        bpy.context.view_layer.update()
    return wp


def cam(loc, target, lens=50):
    c = bpy.data.objects.get("SheetCam")
    if c is None:
        c = bpy.data.objects.new("SheetCam", bpy.data.cameras.new("SheetCam")); sc.collection.objects.link(c)
    c.data.lens = lens; c.data.clip_start = 0.05; c.location = Vector(loc)
    c.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler(); sc.camera = c


sc.render.engine = "BLENDER_WORKBENCH"; sc.display.shading.light = "STUDIO"; sc.display.shading.color_type = "TEXTURE"
sc.display.shading.show_object_outline = True
sc.render.resolution_x, sc.render.resolution_y = W, H; sc.render.resolution_percentage = 100
sc.view_settings.view_transform = "Standard"
tmp = os.path.join(OUT, "_tile.png")


def tile():
    sc.render.filepath = tmp; bpy.ops.render.render(write_still=True)
    im = bpy.data.images.load(tmp, check_existing=False)
    px = np.array(im.pixels[:], dtype=np.float32).reshape(H, W, 4); bpy.data.images.remove(im)
    return px


def bend(wp, a, b, c):
    u = A.vnorm(A.vsub(wp[sk.idx[a]], wp[sk.idx[b]])); v = A.vnorm(A.vsub(wp[sk.idx[c]], wp[sk.idx[b]]))
    return 180 - math.degrees(math.acos(max(-1, min(1, A.vdot(u, v)))))


VIEWS = {"seated": [((1.9, -2.6, 1.6), (0, -0.25, 0.8), 50), ((0, -3.0, 1.1), (0, -0.25, 0.85), 50)],
         "standing": [((3.4, -4.1, 1.8), (0, 0, 0.88), 70), ((0, -5.4, 1.0), (0, 0, 0.88), 70)]}
for clip in A.CLIPS:
    name = clip["name"]
    if ONLY and name not in ONLY: continue
    c = clips[name]; n = len(c["frames"]); fl = c["flags"]
    seated = clip["base"] == "seated"
    if props: props.hide_render = not seated
    picks = sorted({0, n // 3, (2 * n) // 3, n - 1})
    while len(picks) < 4: picks.append(picks[-1])
    sheet = np.ones((H * 2, W * 4, 4), dtype=np.float32)
    worst_bend = 0.0; low = 9.0
    for col, k in enumerate(picks):
        base = seat if seated else (sk.rest_lp, sk.rest_lq)
        lp, lq = apply_tracks(c, k, base[0], base[1], bool(fl & 2))
        wp = set_pose(lp, lq)
        for s in ("_l", "_r"):
            worst_bend = max(worst_bend, bend(wp, "upperarm" + s, "lowerarm" + s, "hand" + s),
                             bend(wp, "thigh" + s, "calf" + s, "foot" + s))
            low = min(low, wp[sk.idx["hand" + s]][1])
        for row, (loc, tg, lens) in enumerate(VIEWS["seated" if seated else "standing"]):
            cam(loc, tg, lens)
            sheet[(1 - row) * H:(2 - row) * H, col * W:(col + 1) * W] = tile()   # image rows go bottom-up
    im = bpy.data.images.new("sheet_" + name, W * 4, H * 2, alpha=True)
    im.pixels[:] = sheet.ravel(); im.filepath_raw = os.path.join(OUT, "clip_%s.png" % name); im.file_format = "PNG"
    im.save()
    sus = worst_bend > 160 or low < 0.0
    print("CHECK %-14s frames %s  max elbow/knee bend %5.1f deg  lowest hand y %.3f m  %s -> %s" %
          (name, picks, worst_bend, low, "SUSPECT" if sus else "ok", im.filepath_raw))
if os.path.exists(tmp): os.remove(tmp)
