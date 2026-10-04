"""Preview renders + checks for the Driver character.

    blender -b driver.blend --python preview.py -- <outdir> [views...] [--cockpit <cockpit_X.dcm> <X.cfg>] [--workbench]

views: standing (front/side/back/34), seated (side/34/top), hands, eye, lod1, cockpit. Default: all except cockpit.
Also prints an intersection report for the seated pose (BVH overlap between body parts / props).
"""
import bpy, bmesh, math, os, sys
from math import radians
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import build_driver as bd

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = argv[0] if argv else os.path.join(HERE, "previews")
os.makedirs(OUT, exist_ok=True)
VIEWS = [a for a in argv[1:] if not a.startswith("--") and not a.endswith((".dcm", ".cfg"))] or \
    ["standing", "seated", "hands", "eye", "lod1"]
WORKBENCH = "--workbench" in argv
sc = bpy.context.scene
arm = bpy.data.objects["Armature"]
O = {n: bpy.data.objects[n] for n in ("Body_LOD0", "Helmet_LOD0", "Visor_LOD0", "Body_LOD1", "Helmet_LOD1", "Visor_LOD1")}
props = bpy.data.collections.get("Props")


def setup_render(w=900, h=900):
    sc.render.resolution_x, sc.render.resolution_y = w, h; sc.render.resolution_percentage = 100
    if WORKBENCH:
        sc.render.engine = "BLENDER_WORKBENCH"; sc.display.shading.light = "STUDIO"; sc.display.shading.color_type = "TEXTURE"
        sc.display.shading.show_object_outline = True
    else:
        sc.render.engine = "BLENDER_EEVEE"
        try: sc.eevee.taa_render_samples = 32
        except Exception: pass
    w_ = sc.world or bpy.data.worlds.new("W"); sc.world = w_
    try: w_.use_nodes = True
    except Exception: pass
    bg = w_.node_tree.nodes.get("Background")
    if bg: bg.inputs["Color"].default_value = (0.42, 0.44, 0.5, 1); bg.inputs["Strength"].default_value = 0.9
    sc.view_settings.view_transform = "Standard"
    if "KeySun" not in bpy.data.objects:
        for nm, rot, en in (("KeySun", (50, 10, 35), 3.0), ("FillSun", (60, 0, -140), 1.2), ("RimSun", (110, 0, 180), 1.5)):
            ld = bpy.data.lights.new(nm, "SUN"); ld.energy = en; lo = bpy.data.objects.new(nm, ld)
            lo.rotation_euler = tuple(radians(x) for x in rot); sc.collection.objects.link(lo)


def cam(loc, target, lens=50, name="PCam", clip=0.05):
    c = bpy.data.objects.get(name)
    if c is None:
        cd = bpy.data.cameras.new(name); c = bpy.data.objects.new(name, cd); sc.collection.objects.link(c)
    c.data.lens = lens; c.data.clip_start = clip
    c.location = Vector(loc); d = Vector(target) - Vector(loc)
    c.rotation_euler = d.to_track_quat("-Z", "Y").to_euler(); sc.camera = c
    return c


def render(name):
    p = os.path.join(OUT, name + ".png"); sc.render.filepath = p
    bpy.ops.render.render(write_still=True); print("PREVIEW", p)


def show(lod=0, props_on=False, pose="Seated_Drive", head=True):
    for k, o in O.items():
        on = k.endswith(str(lod))
        if k.startswith(("Helmet", "Visor")) and not head: on = False
        o.hide_render = not on; o.hide_viewport = not on
    if props: props.hide_render = not props_on
    arm.animation_data.action = bpy.data.actions[pose]
    sc.frame_set(0)


def mask_head(on):
    b = O["Body_LOD0"]; m = b.modifiers.get("HeadMask")
    if on and m is None:
        m = b.modifiers.new("HeadMask", "MASK"); m.vertex_group = "mask_head"; m.invert_vertex_group = True
    if not on and m is not None: b.modifiers.remove(m)


def overlap_report():
    """BVH overlaps between parts of the posed LOD0 body (+ props). Designed overlaps (limb roots inside the torso,
    cuffs over sleeves, boots over legs) are excluded by pairing only parts that should never touch."""
    dg = bpy.context.evaluated_depsgraph_get()
    b = O["Body_LOD0"].evaluated_get(dg); me = b.to_mesh()
    part = [d.value for d in me.attributes["dpart"].data]
    W = b.matrix_world
    groups = {}
    for poly in me.polygons:
        ps = {part[v] for v in poly.vertices}
        if len(ps) != 1: continue
        groups.setdefault(ps.pop(), []).append([W @ me.vertices[v].co for v in poly.vertices])
    # split arms: upper (s<0.25) vs forearm by vertex weight would need groups; use hands/forearm-level parts instead
    trees = {}
    for k, polys in groups.items():
        verts, faces = [], []
        for pl in polys:
            faces.append(list(range(len(verts), len(verts) + len(pl)))); verts += [tuple(v) for v in pl]
        trees[k] = BVHTree.FromPolygons(verts, faces)
    if props:
        for o in props.objects:
            oe = o.evaluated_get(dg); m2 = oe.to_mesh()
            trees[o.name] = BVHTree.FromPolygons([tuple(o.matrix_world @ v.co) for v in m2.vertices], [list(p.vertices) for p in m2.polygons])
            oe.to_mesh_clear()
    P = bd.PART
    pairs = [("hand_l", "hand_r"), ("hand_l", "torso"), ("hand_r", "torso"), ("hand_l", "leg_l"), ("hand_r", "leg_r"),
             ("cuff_l", "torso"), ("cuff_r", "torso"), ("cuff_l", "leg_l"), ("cuff_r", "leg_r"), ("leg_l", "leg_r"),
             ("boot_l", "boot_r"), ("arm_l", "leg_l"), ("arm_r", "leg_r"), ("deltoid_l", "collar"), ("deltoid_r", "collar"),
             ("hand_l", "prop_seat"), ("leg_l", "prop_wheel"), ("leg_r", "prop_wheel"), ("torso", "prop_wheel"),
             ("arm_l", "prop_wheel"), ("arm_r", "prop_wheel"), ("cuff_l", "prop_wheel"), ("cuff_r", "prop_wheel"),
             ("torso", "prop_seat"), ("leg_l", "prop_seat"), ("boot_l", "prop_floor"), ("boot_r", "prop_floor"),
             ("hand_l", "prop_wheel"), ("hand_r", "prop_wheel")]
    out = []
    for a, c in pairs:
        ka = P.get(a, a); kc = P.get(c, c)
        if ka in trees and kc in trees:
            n = len(trees[ka].overlap(trees[kc]))
            out.append("%s/%s=%d" % (a, c, n))
    b.to_mesh_clear()
    print("OVERLAP " + "  ".join(out))


setup_render()
if "standing" in VIEWS:
    show(0, False, "A_Pose")
    for nm, loc in (("front", (0, -5.4, 1.0)), ("side", (5.4, 0, 1.0)), ("back", (0, 5.4, 1.0)), ("34", (3.4, -4.1, 1.8))):
        cam(loc, (0, 0, 0.88), 85); render("standing_" + nm)
if "seated" in VIEWS:
    show(0, True, "Seated_Drive")
    for nm, loc, tg in (("side", (3.2, -0.3, 0.95), (0, -0.25, 0.8)), ("34", (1.9, -2.6, 1.6), (0, -0.25, 0.8)),
                        ("front", (0, -3.0, 1.1), (0, -0.25, 0.85)), ("top", (0.4, -0.4, 3.4), (0, -0.3, 0.8))):
        cam(loc, tg, 50); render("seated_" + nm)
    overlap_report()
if "hands" in VIEWS:
    show(0, True, "Seated_Drive")
    eye, seat, back_y, wheel, X, U, F, rim = bd.generic_targets()
    cam(wheel + Vector((0.55, -0.55, 0.30)), wheel, 50); render("hands_front34")
    cam(wheel + Vector((0.0, 0.42, 0.33)), wheel + Vector((0, 0, -0.02)), 35); render("hands_driver_side")
    cam(wheel + Vector((0.45, 0.0, 0.05)), wheel + Vector((0.12, 0, 0)), 60); render("hands_left_side")
if "eye" in VIEWS:
    show(0, True, "Seated_Drive", head=False); mask_head(True)
    eye = bd.generic_targets()[0]
    c = cam(eye, eye + Vector((0, -1, -0.30)), 18, clip=0.03); c.data.lens_unit = "FOV"; c.data.sensor_fit = "VERTICAL"
    c.data.angle = radians(60); render("eye_view")
    mask_head(False)
if "lod1" in VIEWS:
    show(1, False, "A_Pose"); cam((2.6, -3.2, 1.6), (0, 0, 0.9), 50); render("lod1_standing")
    show(1, True, "Seated_Drive"); cam((1.9, -2.6, 1.6), (0, -0.25, 0.8), 50); render("lod1_seated")
if "detail" in VIEWS:
    I = bd.skeleton_def()[1]["_l"]
    show(0, False, "A_Pose")
    w = I["WR"] + I["a"] * 0.06
    cam(w + Vector((0.05, -0.30, 0.08)), w, 50); render("detail_hand_rest_front")
    cam(w + I["n"] * -0.3, w, 50); render("detail_hand_rest_back")
    cam(w + I["n"] * 0.3, w, 50); render("detail_hand_rest_palm")
    cam((0.45, -0.45, 0.25), (0.1, -0.05, 0.08), 50); render("detail_boot")
    cam((0.0, -0.9, 1.75), (0, 0, 1.45), 50); render("detail_collar")
    show(0, True, "Seated_Drive")
    if props: props.hide_render = True
    cam((0.55, -1.25, 0.45), (0.0, -0.15, 0.60), 50); render("detail_seated_hip_front")
    cam((0.9, 0.2, 0.55), (0.05, -0.1, 0.6), 50); render("detail_seated_hip_side")
    cam((0.0, -0.4, 0.15), (0.0, -0.05, 0.6), 40); render("detail_seated_below")
if "handortho" in VIEWS:
    I = bd.skeleton_def()[1]["_l"]
    show(0, False, "A_Pose")
    w = I["WR"] + I["a"] * 0.08
    for nm, d in (("back", -I["n"]), ("palm", I["n"]), ("thumbside", I["t"]), ("pinkyside", -I["t"])):
        c = cam(w + d * 0.6, w, 50); c.data.type = "ORTHO"; c.data.ortho_scale = 0.26; render("handortho_" + nm)
        c.data.type = "PERSP"
if "--cockpit" in argv:
    # Seat the driver in a real DriverCam cockpit (read-only): same maths as the plugin spec (eye = e + Driver.Offset,
    # wheel = modelScale*(pivot + Part.SteeringWheel move), rim 0.185*size*modelScale, seat from RL_SeatDriver_* parts).
    dcm_path, cfg_path = argv[argv.index("--cockpit") + 1], argv[argv.index("--cockpit") + 2]
    car = os.path.basename(dcm_path)[8:-4]
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(dcm_path)), "interiors"))
    import dcm_io
    from mathutils import Quaternion, Euler
    cfg = {}
    for ln in open(cfg_path, encoding="utf-8"):
        if "=" in ln and not ln.lstrip().startswith("#"):
            k, v = ln.split("=", 1); cfg[k.strip()] = v.strip()
    def f3(k, d=(0, 0, 0)): return Vector([float(x) for x in cfg.get(k, " ".join(map(str, d))).split()[:3]]) if k in cfg else Vector(d)
    def part(k):
        v = [float(x) for x in cfg.get(k, "0 0 0 0 0 0 1").split()]; return Vector(v[0:3]), Vector(v[3:6]), v[6]
    def euler_u(r):   # Unity Quaternion.Euler(x, y, z): z, then x, then y
        return (Matrix.Rotation(radians(r.y), 3, "Y") @ Matrix.Rotation(radians(r.x), 3, "X") @ Matrix.Rotation(radians(r.z), 3, "Z"))
    m = dcm_io.parse(dcm_path)
    ms = float(cfg.get("View.CockpitScale", "1"))
    eye_u = Vector(m["eye"]) + Vector((float(cfg.get("Driver.OffsetX", 0)), float(cfg.get("Driver.OffsetY", 0)), float(cfg.get("Driver.OffsetZ", 0))))
    wp = [p_ for p_ in m["parts"] if p_["name"] == "SteeringWheel"][0]
    Pp, Fp, Up = (Vector(x) for x in wp["pivot"]); Fp.normalize(); Up.normalize()
    mv, rt, sz = part("Part.SteeringWheel")
    Rw = euler_u(rt)
    wheel_u = (Pp + mv) * ms; F_u = Rw @ Fp; U_u = Rw @ Up; X_u = U_u.cross(F_u).normalized()
    rim = 0.185 * sz * ms
    def verts(name):
        pp = [p_ for p_ in m["parts"] if p_["name"] == name][0]
        return [Vector(v) for tris in pp["tags"].values() for tri in tris for (v, n_, uv) in tri]
    cv = verts("RL_SeatDriver_Cushion"); bvv = verts("RL_SeatDriver_Back")
    seat_u = Vector(((min(v.x for v in cv) + max(v.x for v in cv)) / 2, max(v.y for v in cv), (min(v.z for v in cv) + max(v.z for v in cv)) / 2)) * ms
    back_u = Vector(((min(v.x for v in bvv) + max(v.x for v in bvv)) / 2, min(v.y for v in bvv), max(v.z for v in bvv))) * ms
    cu = bd.char_from_unity
    eye, seat, back_y, wheel = cu(eye_u), cu(seat_u), cu(back_u).y, cu(wheel_u)
    X, U, F = cu(X_u), cu(U_u), cu(F_u)
    s_ = max(0.85, min(1.3, (eye_u.y - seat_u.y) / 0.74))
    print("COCKPIT %s eye %s seatTop %s backFront z %.3f wheel %s (rel eye %s) rim %.3f steer %s scale s=%.3f" % (
        car, tuple(round(x, 3) for x in eye_u), tuple(round(x, 3) for x in seat_u), back_u.z, tuple(round(x, 3) for x in wheel_u),
        tuple(round(x, 3) for x in wheel_u - eye_u), rim, cfg.get("View.SteerAngle"), s_))
    B_, info = bd.skeleton_def()
    Pz = bd.Pose(arm, info["order"]); lg = {}
    # solve in the driver's own scale: targets / s, armature scaled by s
    bd.solve_seated(Pz, info, eye / s_, seat / s_, back_y / s_, wheel / s_, X, U, F, rim / s_, log=lg)
    print("COCKPIT fit", {k: ([round(x, 3) for x in v] if hasattr(v, "__len__") else round(v, 3)) for k, v in lg.items()})
    arm.animation_data.action = None; Pz.apply(arm)
    arm.scale = (s_, s_, s_); arm.rotation_euler = (0, 0, math.pi)   # character -Y forward -> cockpit scene +Y forward
    if props: props.hide_render = True
    coll = bpy.data.collections.new("Cockpit"); sc.collection.children.link(coll)
    objs = dcm_io.to_blender(m, coll, mats=m["mats"])
    for mt in bpy.data.materials:
        if mt.name.startswith("dcm_") and mt.node_tree and mt.node_tree.nodes.get("Principled BSDF"):
            mt.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = mt.diffuse_color
    grp = {p_["name"]: p_["group"] for p_ in m["parts"]}
    for o in objs:
        g = grp.get(o.name.split(".")[0], "")
        mv_, rt_, sz_ = part("Part." + g) if ("Part." + g) in cfg else (Vector(), Vector(), 1.0)
        if g == "SteeringWheel":
            # rotate/scale about the pivot, then move (cockpit layout), all times modelScale
            o.data.transform(Matrix.Translation(dcm_io.u2b(*Pp)) @ Matrix.Diagonal((sz_,) * 3).to_4x4() @ Matrix.Translation(-dcm_io.u2b(*Pp)))
        o.location = dcm_io.u2b(*(mv_ * ms))
        o.scale = (ms, ms, ms)
    for k in ("Body", "Helmet", "Visor"):
        O[k + "_LOD0"].hide_render = False; O[k + "_LOD1"].hide_render = True
    hide = {"Shell", "Doors", "Roof", "Pillars", "MirrorLeft", "MirrorRight", "RearMirror"}
    for o in objs: o.hide_render = grp.get(o.name.split(".")[0], "") in hide
    eb = dcm_io.u2b(*eye_u)
    cam(eb + Vector((2.4, 0.35, 0.05)), eb + Vector((0, 0.35, -0.45)), 40); render("cockpit_%s_side" % car)
    cam(eb + Vector((1.3, 1.9, 0.6)), eb + Vector((0, 0.2, -0.45)), 40); render("cockpit_%s_front34" % car)
    for o in objs: o.hide_render = False
    for k in ("Helmet_LOD0", "Visor_LOD0"): O[k].hide_render = True
    mask_head(True)
    c = cam(eb, eb + Vector((0, 1, -0.12)), 16, clip=0.03); c.data.lens_unit = "FOV"; c.data.sensor_fit = "VERTICAL"
    c.data.angle = radians(float(cfg.get("View.Fov", 60))); render("cockpit_%s_eye" % car)
    mask_head(False)
