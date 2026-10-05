"""Previews of the ride_sportbike pose on a bike stand-in built from the Bikes plugin's sockets (anim_clips.BIKES).
The pose comes from driver_anims.dra and is fitted with anim_clips.ride_fit / ride_frame, the same steps as the
plugin's Solver.FitBike / FrameBike (without the clip layers). Workbench renders, one sheet per scenario.

    blender -b driver.blend --python preview_ride.py -- <outdir> [--dra driver_anims.dra] [--csm <dir with *.csm>]

--csm adds the real bike model (Bikes' BMW_S1000RR.csm / SportBike.csm, read-only, never written) as a second check of
the sockets. Each sheet: side, 3/4 front, front, top. Prints per scenario the IK shortfalls, knee and elbow
positions and the nearest stand-in distance of the knees (CHECK ... ok / SUSPECT).
"""
import bpy, bmesh, math, os, sys
from math import radians
from mathutils import Matrix, Quaternion, Vector
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import drm_io, anim_clips as A   # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = argv[0]; os.makedirs(OUT, exist_ok=True)
DRA = argv[argv.index("--dra") + 1] if "--dra" in argv else os.path.join(HERE, "driver_anims.dra")
CSM = argv[argv.index("--csm") + 1] if "--csm" in argv else None
C = Matrix(((-1, 0, 0), (0, 0, 1), (0, -1, 0)))          # Blender -> Unity
CT = C.transposed()
W, H = 420, 420

d = drm_io.read_drm(os.path.join(HERE, "driver.drm"))
names = [b[0] for b in d["skel"]]; parents = [b[1] for b in d["skel"]]
sk = A.Skel(names, parents, [b[2] for b in d["skel"]], [b[3] for b in d["skel"]])
socks = A.socks_of(d)
rest_wp, rest_wq = sk.fk(sk.rest_lp, sk.rest_lq)
clips = {c["name"]: c for c in drm_io.read_dra(DRA)}
if "ride_sportbike" not in clips: raise SystemExit("RESULT: FAIL no ride_sportbike in " + DRA)
base_lp, base_lq = list(sk.rest_lp), list(sk.rest_lq)
for (b, ch), (q, p) in zip(clips["ride_sportbike"]["tracks"], clips["ride_sportbike"]["frames"][0]):
    i = sk.idx[b]
    if ch & 1: base_lq[i] = A.qnorm(q)
    if ch & 2: base_lp[i] = tuple(p)

sc = bpy.context.scene
arm = bpy.data.objects["Armature"]
if arm.animation_data: arm.animation_data.action = None
for o in bpy.data.objects:
    if o.type == "MESH" and o.name.endswith("_LOD1"): o.hide_render = True
props = bpy.data.collections.get("Props")
if props: props.hide_render = True
Aw0 = arm.matrix_world.copy()


def U2B(v): return CT @ Vector(v)


def set_pose(lp, lq, lean_m):
    """lean_m: the bike lean as a Blender 4x4 about the ground origin (the rider is parented to the leaning frame)."""
    arm.matrix_world = lean_m @ Aw0
    bpy.context.view_layer.update()
    Aw = Aw0   # pose bones are set in armature space; the lean is on the object
    wp, wq = sk.fk(lp, lq)
    for i, n in enumerate(names):
        pb = arm.pose.bones.get(n)
        if pb is None: continue
        Du = Quaternion((wq[i][3], wq[i][0], wq[i][1], wq[i][2])) @ \
            Quaternion((rest_wq[i][3], rest_wq[i][0], rest_wq[i][1], rest_wq[i][2])).inverted()
        Db = CT @ Du.to_matrix() @ C
        R = Db @ arm.data.bones[n].matrix_local.to_3x3()
        M = R.to_4x4(); M.translation = Aw.inverted() @ (CT @ Vector(wp[i]))
        pb.matrix = M
        bpy.context.view_layer.update()
    return wp


# ---------------------------------------------------------------------------------------------- the bike stand-in
def mat(name, rgb):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.diffuse_color = (*rgb, 1.0)
    return m


def box(bm, center_u, size_u, rot_x_deg=0.0):
    r = bmesh.ops.create_cube(bm, size=1.0)
    M = Matrix.Translation(U2B(center_u)) @ Matrix.Rotation(radians(rot_x_deg), 4, "X") @ \
        Matrix.Diagonal((size_u[0], size_u[2], size_u[1], 1))
    bmesh.ops.transform(bm, matrix=M, verts=r["verts"])


def cyl(bm, a_u, b_u, rad, seg=12):
    a, b = U2B(a_u), U2B(b_u); dvec = b - a
    r = bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=rad, radius2=rad, depth=dvec.length)
    M = Matrix.Translation((a + b) * 0.5) @ dvec.to_track_quat("Z", "Y").to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=M, verts=r["verts"])


def make_standin(key):
    """A plain bike from the sockets only: wheels, frame spar, tank, seat, tail, clip-ons, pegs."""
    b = A.BIKES[key]; s = b["seat"]; g = b["grip"]; p = b["peg"]
    bm = bmesh.new()
    for z in (0.71, -0.71):   # wheels (r 0.30, as both models' pivots)
        r = bmesh.ops.create_cone(bm, cap_ends=True, segments=28, radius1=0.30, radius2=0.30, depth=0.17)
        bmesh.ops.transform(bm, matrix=Matrix.Translation(U2B((0, 0.30, z))) @ Matrix.Rotation(radians(90), 4, "Y"), verts=r["verts"])
    box(bm, (0, s[1] - 0.04, s[2] - 0.02), (0.26, 0.06, 0.30))                 # seat pad
    box(bm, (0, s[1] + 0.02, s[2] - 0.40), (0.20, 0.10, 0.40), -8)             # tail
    tz0, tz1 = s[2] + 0.17, g[2] - 0.04                                         # tank between the seat and the bars
    box(bm, (0, s[1] + 0.03, (tz0 + tz1) * 0.5), (0.36, 0.26, tz1 - tz0), 6)
    box(bm, (0, 0.52, -0.02), (0.30, 0.34, 0.70))                               # engine / fairing sides
    cyl(bm, (0, g[1] + 0.02, g[2]), (0, 0.30, 0.71), 0.03)                       # fork
    for sg in (-1, 1):
        cyl(bm, (sg * 0.06, g[1], g[2]), (sg * g[0] + sg * 0.06, g[1], g[2]), 0.017)   # clip-on + grip
        cyl(bm, (sg * 0.08, p[1], p[2]), (sg * p[0] + sg * 0.05, p[1], p[2]), 0.012)   # peg
    me = bpy.data.meshes.new("bike_" + key); bm.to_mesh(me); bm.free()
    me.materials.append(mat("bike_grey", (0.55, 0.57, 0.62)))
    o = bpy.data.objects.new("bike_" + key, me); sc.collection.objects.link(o)
    return o


def load_csm(path):
    """Bikes' .csm (Unity axes): Body + wheels (wheel triangles are written around their pivot)."""
    verts = []; faces = []; pivot = Vector((0, 0, 0)); part = None
    for raw in open(path):
        t = raw.split()
        if not t or t[0].startswith("#"): continue
        if t[0] == "o": part = t[1]; pivot = Vector((0, 0, 0))
        elif t[0] == "p": pivot = Vector((float(t[1]), float(t[2]), float(t[3])))
        elif t[0] in ("f", "u"):
            st = 8 if t[0] == "u" else 6; ids = []
            for k in range(3):
                o = 1 + k * st
                v = Vector((float(t[o]), float(t[o + 1]), float(t[o + 2]))) + pivot
                ids.append(len(verts)); verts.append(CT @ v)
            faces.append(ids)
    me = bpy.data.meshes.new("csm"); me.from_pydata([tuple(v) for v in verts], [], faces); me.update()
    me.materials.append(mat("bike_csm", (0.2, 0.35, 0.75)))
    o = bpy.data.objects.new(os.path.basename(path), me); sc.collection.objects.link(o)
    return o, verts


# ---------------------------------------------------------------------------------------------- render
sc.render.engine = "BLENDER_WORKBENCH"; sc.display.shading.light = "STUDIO"; sc.display.shading.color_type = "MATERIAL"
sc.display.shading.show_object_outline = True
sc.render.resolution_x, sc.render.resolution_y = W, H; sc.render.resolution_percentage = 100
sc.view_settings.view_transform = "Standard"
for o in (bpy.data.objects.get(n) for n in ("Body_LOD0", "Helmet_LOD0", "Visor_LOD0")):
    if o is not None:
        for s_ in o.material_slots:
            if s_.material: s_.material.diffuse_color = (0.85, 0.35, 0.55, 1) if o.name.startswith("Body") else (0.1, 0.1, 0.12, 1)
tmp = os.path.join(OUT, "_tile.png")


def cam(loc_u, target_u, lens=45):
    c = bpy.data.objects.get("RideCam")
    if c is None:
        c = bpy.data.objects.new("RideCam", bpy.data.cameras.new("RideCam")); sc.collection.objects.link(c)
    c.data.lens = lens; c.data.clip_start = 0.05
    loc, tg = U2B(loc_u), U2B(target_u)
    c.location = loc; c.rotation_euler = (tg - loc).to_track_quat("-Z", "Y").to_euler(); sc.camera = c


def tile():
    sc.render.filepath = tmp; bpy.ops.render.render(write_still=True)
    im = bpy.data.images.load(tmp, check_existing=False)
    px = np.array(im.pixels[:], dtype=np.float32).reshape(H, W, 4); bpy.data.images.remove(im)
    return px


VIEWS = [((3.2, 1.0, 0.0), (0, 0.85, 0.0), 45),        # side, from the right
         ((2.2, 1.6, 2.6), (0, 0.85, 0.05), 45),        # 3/4 front
         ((0.0, 1.0, 3.6), (0, 0.85, 0.0), 45),         # front
         ((0.0, 4.2, -0.05), (0, 0.8, 0.0), 45)]        # top (from above)


def lean_matrix(lean_deg):
    """Unity rotation about +z (forward) by lean (+ = left), as a Blender matrix about the ground origin."""
    q = A.qaxis((0, 0, 1), lean_deg)
    Ru = Quaternion((q[3], q[0], q[1], q[2])).to_matrix()
    return (CT @ Ru @ C).to_4x4()


def sheet(name, key, lean=0.0, hang=True, real=None):
    stand = make_standin(key)
    objs = [stand] + ([real] if real is not None else [])
    for o in bpy.data.objects:
        if o.name.startswith("bike_") or o.name.endswith(".csm"): o.hide_render = o not in objs
    Lm = lean_matrix(lean)
    for o in objs: o.matrix_world = Lm
    bike = A.BIKES[key]
    R, rep = A.ride_fit(sk, socks, base_lp, base_lq, bike)
    rep.update(A.ride_frame(R, bike, lean, hang))
    set_pose(R.lp, R.lq, Lm)
    img = np.ones((H * 2, W * 2, 4), dtype=np.float32)
    sh = A.vsub(A.qrot(A.qaxis((0, 0, 1), lean), (0, 0.85, 0)), (0, 0.85, 0))   # the cameras follow the leaning rider
    for k, (loc, tg, lens) in enumerate(VIEWS):
        cam(A.vadd(loc, sh), A.vadd(tg, sh), lens)
        r, c_ = divmod(k, 2)
        img[(1 - r) * H:(2 - r) * H, c_ * W:(c_ + 1) * W] = tile()
    im = bpy.data.images.new("ride_" + name, W * 2, H * 2, alpha=True)
    im.pixels[:] = img.ravel(); im.filepath_raw = os.path.join(OUT, "ride_%s.png" % name); im.file_format = "PNG"; im.save()
    worst = max(v[0] for k, v in rep.items() if k.startswith(("arm", "leg")))
    eye = R.sock("eye_c")
    print("CHECK %-22s short %.1f cm  eye %s  hang %s  knees %s %s  elbows %s %s  %s -> %s" %
          (name, worst, tuple(round(x, 2) for x in eye), rep["hang"], rep.get("leg_l", ("", ""))[1], rep.get("leg_r", ("", ""))[1],
           rep["arm_l"][1], rep["arm_r"][1], "SUSPECT" if worst > 1.0 else "ok", im.filepath_raw))
    bpy.data.objects.remove(stand, do_unlink=True)


real = {}
if CSM:
    for key, fn in (("S1000RR", "BMW_S1000RR.csm"), ("SportBike", "SportBike.csm")):
        pth = os.path.join(CSM, fn)
        if os.path.exists(pth):
            o, vs = load_csm(pth); real[key] = o
            # seat surface near the seat socket (sanity check of the sockets against the model)
            s = A.BIKES[key]["seat"]
            ys = [(C @ v).y for v in vs if abs((C @ v).x - s[0]) < 0.05 and abs((C @ v).z - s[2]) < 0.05]
            print("CHECK csm %-9s top of the model at the seat socket: y %.3f (socket %.3f)" % (key, max(ys) if ys else -1, s[1]))

sheet("s1000rr", "S1000RR")
sheet("sportbike", "SportBike")
sheet("s1000rr_lean_right40", "S1000RR", lean=-40.0)
sheet("s1000rr_lean_left25", "S1000RR", lean=25.0)
sheet("s1000rr_lean_right40_nohang", "S1000RR", lean=-40.0, hang=False)
for key, o in real.items():
    sheet("%s_model" % key.lower(), key, real=o)
    sheet("%s_model_lean_right40" % key.lower(), key, lean=-40.0, real=o)
if os.path.exists(tmp): os.remove(tmp)
print("RESULT: OK")
