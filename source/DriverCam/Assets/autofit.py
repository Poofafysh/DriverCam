"""
DriverCam auto-fit: builds a fitted cockpit for a Driving Rogue car from its dumped prefab (VehicleAssets/Prefabs).

Run inside Blender (cockpit.blend, which holds the low-poly reference car):
    exec(open(r"...\\DriverCam\\Assets\\autofit.py").read()); fit("Centaur", "Centaur_body.glb")

Work frame: the prefab as Blender imports it -> +Y forward, +X right, +Z up, meters, origin = car body pivot.
Unity body frame = (x, z, y) of the work frame (verified against the in-game Saber export: same body, hood forward).
All sizes are scaled from the hand-made Saber fit by k = car width / Saber width.
"""
import bpy, bmesh, math, os
import numpy as np
from mathutils import Vector, Matrix

VA = r"C:\Users\surfi\OneDrive\Desktop\rogue driver mods\VehicleAssets\Prefabs"
OUT = r"C:\Users\surfi\OneDrive\Desktop\rogue driver mods\DriverCam\Assets\cockpits"
SABER_WIDTH, SABER_HEIGHT = 1.774, 1.198   # the Saber in the units the hand fit was made in

PLAYER_CARS = {
    "Bond": "Bond_Body.glb", "Centaur": "Centaur_body.glb", "Centipede": "Centipede_Body.glb",
    "Delivery": "Delivery_Body.glb", "Justice": "Justice_Body.glb", "Phoenix": "Phoenix_Body.glb",
    "Rotary": "Rotary_Body.glb", "Saber": "Saber_Body.glb", "Shadow": "Shadow_Body.glb", "Vektor": "Vektor_Body.glb",
}


def u(v): return Vector((v.x, v.z, v.y))
def fmt(v): return " ".join(f"{c:.5f}" for c in v)
def mat(tag): return bpy.data.materials.get(tag) or bpy.data.materials.new(tag)


def tag_for(n):
    n = (n or "").lower()
    if n in ("interior_mid", "interior_dark", "glass", "metal", "trim_dark", "paint", "accent",
             "mirror_glass", "mirror_left", "mirror_right"):
        return n
    if "interiorpanel.001" in n: return "interior_dark"
    if "interiorpanel" in n: return "interior_mid"
    if any(k in n for k in ("handler", "rim", "mirror")): return "metal"
    if "chassis" in n: return "paint"
    if "window" in n: return "glass"
    if "bumper" in n or "tire" in n: return "trim_dark"
    return "interior_dark"


def import_car(glb):
    sc = bpy.data.scenes.get("AutoFit") or bpy.data.scenes.new("AutoFit")
    win = bpy.context.window
    prev = win.scene
    win.scene = sc
    try:
        for o in list(sc.objects):
            bpy.data.objects.remove(o, do_unlink=True)
        before = set(bpy.data.objects)
        import contextlib, io, logging
        logging.disable(logging.INFO)
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                bpy.ops.import_scene.gltf(filepath=os.path.join(VA, glb))
        finally:
            logging.disable(logging.NOTSET)
        new = [o for o in bpy.data.objects if o not in before]
        # The import faces -Y; turn it to +Y forward. Unity body = (-x, z, -y) of the import = (x, z, y) of the work
        # frame (matches the in-game Saber export to ~1 mm). Update while this scene is active so children follow.
        R = Matrix.Rotation(math.pi, 4, "Z")
        for r in [o for o in new if o.parent is None]:
            r.matrix_world = R @ r.matrix_world
        bpy.context.view_layer.update()
    finally:
        win.scene = prev
    bodies = [o for o in new if o.type == "MESH" and "_body" in o.name.lower()
              and not any(k in o.name.lower() for k in ("glitch", "decal", "ghost"))]
    return sc, max(bodies, key=lambda o: len(o.data.vertices))


def measure(body):
    mw = body.matrix_world
    nm = mw.to_3x3().inverted_safe().transposed()
    V = np.array([list(mw @ v.co) for v in body.data.vertices])
    xmin, xmax = V[:, 0].min(), V[:, 0].max()
    CX, W = (xmin + xmax) / 2, xmax - xmin
    zmin = V[:, 2].min()
    k = W / SABER_WIDTH
    step = 0.05 * k
    bins = {}
    for x, y, z in V[np.abs(V[:, 0] - CX) < 0.08 * W]:
        b = round(y / step)
        bins[b] = max(bins.get(b, -1e9), z)
    # ignore slices that only caught the underside (sparse vertices make them look like cliffs)
    zmax = V[:, 2].max()
    raw = [(b * step, bins[b]) for b in sorted(bins) if bins[b] - zmin > 0.45 * (zmax - zmin)]
    prof = [(raw[i][0], float(np.mean([p[1] for p in raw[max(0, i - 1):i + 2]]))) for i in range(len(raw))]
    i_roof = int(np.argmax([p[1] for p in prof]))
    roof = prof[i_roof][1]
    H = roof - zmin
    # walk forward from the roof: windshield = first steep drop, base = where it flattens into the hood
    i_top = i_base = None
    for i in range(i_roof, len(prof) - 1):
        slope = (prof[i + 1][1] - prof[i][1]) / (prof[i + 1][0] - prof[i][0])
        if i_top is None and slope < -0.55:
            i_top = i
        if i_top is not None and slope > -0.3 and prof[i][1] < roof - 0.15 * H:
            i_base = i
            break
    faces = [(mw @ p.center, (nm @ p.normal).normalized(), p) for p in body.data.polygons]
    verts = [mw @ v.co for v in body.data.vertices]

    # The windshield itself: faces in the middle of the car, high up, tilted forward-and-up at a windshield angle
    # (hood is nearly flat, roof flat, grille vertical). Its extent gives the top/base edges directly.
    ymin, ymax = V[:, 1].min(), V[:, 1].max()
    glass = [(c, n, p) for c, n, p in faces
             if abs(c.x - CX) < 0.3 * W and c.z > zmin + 0.6 * H and 0.3 < n.y < 0.85 and n.z > 0.45
             and c.y > ymin + 0.35 * (ymax - ymin) and c.y < ymax - 0.12 * (ymax - ymin)]
    if len(glass) >= 3:
        gv = [verts[vi] for c, n, p in glass for vi in p.vertices if abs(verts[vi].x - CX) < 0.3 * W]
        top = [v for v in gv if v.z > max(g.z for g in gv) - 0.03 * k]
        bot = [v for v in gv if v.z < min(g.z for g in gv) + 0.03 * k]
        y_wt, z_wt = float(np.mean([v.y for v in top])), float(np.mean([v.z for v in top]))
        y_wb, z_wb = float(np.mean([v.y for v in bot])), float(np.mean([v.z for v in bot]))
    elif i_top is not None and i_base is not None:
        y_wt, z_wt = prof[i_top]
        y_wb, z_wb = prof[i_base]
    else:
        raise RuntimeError("couldn't find the windshield")
    edges = {-1: [], 1: []}
    for z0 in np.linspace(z_wb + 0.1 * (z_wt - z_wb), z_wt - 0.1 * (z_wt - z_wb), 8):
        for side in (-1, 1):
            cand = [verts[vi] for c, n, p in faces
                    if abs(c.z - z0) < 0.04 * k and n.y > 0.25 and n.z > 0.2 and (c.x - CX) * side >= 0
                    for vi in p.vertices]
            cand = [v for v in cand if abs(v.z - z0) < 0.05 * k]
            if cand:
                edges[side].append(max(cand, key=lambda v: (v.x - CX) * side))
    # if the side-edge trace is thin, fall back to the windshield's outer corners at its top and base
    if len(glass) >= 3:
        for side in (-1, 1):
            if len(edges[side]) < 3:
                sv_ = [verts[vi] for c, n, p in glass for vi in p.vertices if (verts[vi].x - CX) * side >= 0]
                if sv_:
                    lo = max([v for v in sv_ if v.z < z_wb + 0.25 * (z_wt - z_wb)] or sv_, key=lambda v: (v.x - CX) * side)
                    hi = max([v for v in sv_ if v.z > z_wt - 0.25 * (z_wt - z_wb)] or sv_, key=lambda v: (v.x - CX) * side)
                    edges[side] = [lo, (lo + hi) / 2, hi]
    mirrors = {-1: [], 1: []}
    for c, n, p in faces:
        if (n.y < -0.55 and abs(c.x - CX) > 0.40 * W and z_wb - 0.25 * k < c.z < z_wb + 0.2 * k
                and y_wt - 0.1 * k < c.y < y_wb + 0.2 * k):
            mirrors[-1 if c.x < CX else 1].append((p, n))
    return dict(CX=CX, W=W, k=k, zmin=zmin, roof=roof, H=H, y_wt=y_wt, z_wt=z_wt, y_wb=y_wb, z_wb=z_wb,
                edges=edges, mirrors=mirrors, mw=mw, verts=verts)


def add_obj(coll, name, bm, tag):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    o = bpy.data.objects.new(name, me)
    coll.objects.link(o)
    o.data.materials.append(mat(tag))
    return o


def chamfer(o, width):
    if width > 0:
        b = o.modifiers.new("Chamfer", "BEVEL")
        b.width = width
        b.segments = 1
        b.limit_method = "ANGLE"
        b.angle_limit = math.radians(30)


def box(coll, name, center, size, tag, bevel, rot_x=0.0):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    R = Matrix.Rotation(math.radians(rot_x), 3, "X")
    for v in bm.verts:
        v.co = Vector(center) + R @ Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
    o = add_obj(coll, name, bm, tag)
    chamfer(o, bevel)
    return o


def beam(coll, name, p0, p1, w, d, inward, tag, bevel):
    axis = (p1 - p0).normalized()
    n = (inward - axis * inward.dot(axis)).normalized()
    b = axis.cross(n).normalized()
    bm = bmesh.new()
    r0, r1 = [[bm.verts.new(p + b * sx * w / 2 + n * sy * d / 2) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
              for p in (p0, p1)]
    for i in range(4):
        bm.faces.new((r0[i], r0[(i + 1) % 4], r1[(i + 1) % 4], r1[i]))
    bm.faces.new(r0[::-1])
    bm.faces.new(r1)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    o = add_obj(coll, name, bm, tag)
    chamfer(o, bevel)
    return o


def copy_ref(coll, src_name, M, name):
    src = bpy.data.objects[src_name]
    o = src.copy()
    o.data = src.data.copy()
    o.name = name
    for c in list(o.users_collection):
        c.objects.unlink(o)
    coll.objects.link(o)
    o.data.transform(M @ src.matrix_world)
    o.matrix_world = Matrix.Identity(4)
    return o


def fit(car, glb=None, suffix=""):
    glb = glb or PLAYER_CARS[car]
    sc, body = import_car(glb)
    m = measure(body)
    k, CX, Wd = m["k"], m["CX"], m["W"]
    name = "AF_" + car + suffix
    coll = bpy.data.collections.get(name)
    if coll:
        for o in list(coll.objects):
            bpy.data.objects.remove(o, do_unlink=True)
    else:
        coll = bpy.data.collections.new(name)
    if coll.name not in sc.collection.children:
        sc.collection.children.link(coll)
    belt = m["z_wb"] - 0.05 * k
    roof_under = m["roof"] - 0.042 * k

    # interior + wheel from the low-poly reference car, dash tucked under the windshield base
    sx = 0.42 * Wd / SABER_WIDTH
    sz = 0.34 * m["H"] / SABER_HEIGHT
    ty = m["y_wb"] + 0.107 * k - 2.719 * sx
    tz = m["z_wb"] - 0.04 * k - 3.159 * sz
    M = Matrix.Translation((CX, ty, tz)) @ Matrix.Diagonal((sx, sx, sz, 1.0)) @ Matrix.Rotation(math.pi, 4, "Z")
    copy_ref(coll, "Interior", M, "AF_Interior")
    wheel = copy_ref(coll, "Steerwheel", M, "AF_Steerwheel")
    WV = np.array([list(v.co) for v in wheel.data.vertices])
    wc = Vector(WV.mean(axis=0))
    eye = Vector((wc.x, wc.y - 0.55 * k, min(wc.z + 0.38 * k, roof_under - 0.12 * k)))

    # lower shell: the car's own body below the window line (floor, footwells, lower sides)
    shell = body.copy()
    shell.data = body.data.copy()
    shell.name = "AF_Shell"
    for c in list(shell.users_collection):
        c.objects.unlink(shell)
    coll.objects.link(shell)
    bm = bmesh.new()
    bm.from_mesh(shell.data)
    bm.transform(body.matrix_world)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    y0, y1, zf = m["y_wb"] - 2.4 * k, m["y_wb"] - 0.08 * k, m["z_wb"] - 0.52 * k
    bmesh.ops.delete(bm, geom=[f for f in bm.faces
                               if not (y0 < f.calc_center_median().y < y1 and f.calc_center_median().z > zf)], context="FACES")
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=Vector((0, 0, belt)),
                           plane_no=Vector((0, 0, 1)), clear_outer=True)
    bm.normal_update()
    for v in bm.verts:
        v.co -= v.normal * 0.018 * k
    bmesh.ops.reverse_faces(bm, faces=bm.faces)
    shell.matrix_world = Matrix.Identity(4)
    bm.to_mesh(shell.data)
    bm.free()
    shell.data.materials.clear()
    shell.data.materials.append(mat("interior_mid"))
    sv = [v.co.copy() for v in shell.data.vertices]

    # inner door panels, cut at the window line and slid flush against the shell
    for sd in ("L", "R"):
        for part in ("F", "R"):
            o = copy_ref(coll, f"{part}{sd}_Door", M, f"AF_Door{sd}_{part}")
            bm = bmesh.new()
            bm.from_mesh(o.data)
            mats = o.data.materials
            side = -1 if np.mean([v.co.x for v in bm.verts]) < CX else 1
            outer = max((v.co.x - CX) * side for v in bm.verts)
            skin = 0.08 * sx * 4.35   # ref door outer skin thickness band, scaled
            kill = [f for f in bm.faces if f.calc_center_median().z > belt
                    or (mats[f.material_index].name if mats[f.material_index] else "") in ("Windows", "Mirror")
                    or (f.calc_center_median().x - CX) * side > outer - skin * 0.5]
            bmesh.ops.delete(bm, geom=kill, context="FACES")
            bm.to_mesh(o.data)
            bm.free()
            vs = [v.co for v in o.data.vertices]
            if not vs:
                bpy.data.objects.remove(o, do_unlink=True)
                continue
            ylo, yhi = min(v.y for v in vs), max(v.y for v in vs)
            wall = [v.x for v in sv if ylo < v.y < yhi and v.z < belt and (v.x - CX) * side > 0.35 * Wd]
            if wall:
                wx = float(np.median(wall))
                o_x = min(v.x for v in vs) if side < 0 else max(v.x for v in vs)
                shift = (wx - o_x) - side * 0.004 * k
                for v in o.data.vertices:
                    v.co.x += shift

    # straight A-pillars along the windshield's side edges, from inside the dash up to the roof lining
    for side, sd in ((-1, "L"), (1, "R")):
        pts = sorted(m["edges"][side], key=lambda p: p.z)
        d = None
        if len(pts) >= 2:
            # pillar = windshield's outer bottom corner -> outer top corner (wrap-around glass spreads the trace
            # sideways, so a best-fit line through all points can lie almost flat)
            lo, hi = pts[0], pts[-1]
            d = (hi - lo).normalized()
            c = (lo + hi) / 2 + Vector((side * 0.02 * k, 0, 0))
            if abs(d.x) > 0.55 or d.z < 0.25:   # still too sideways / flat: use the windshield corners instead
                d = None
        if d is None:
            c = Vector((CX + side * 0.36 * Wd, (m["y_wb"] + m["y_wt"]) / 2, (m["z_wb"] + m["z_wt"]) / 2))
            d = Vector((0, m["y_wt"] - m["y_wb"], m["z_wt"] - m["z_wb"])).normalized()
        p0 = c + d * ((belt - 0.05 * k - c.z) / d.z)
        p1 = c + d * ((roof_under - c.z) / d.z)
        inward = Vector((eye.x - (p0.x + p1.x) / 2, 0, 0)).normalized()
        beam(coll, "AF_APillar" + sd, p0, p1, 0.05 * k, 0.035 * k, inward, "interior_mid", 0.008 * k)

    # roof lining, header, visors, rear-view mirror
    yt, rf = m["y_wt"], m["roof"]
    box(coll, "AF_Header", (CX, yt - 0.02 * k, rf - 0.055 * k), (0.59 * Wd, 0.08 * k, 0.04 * k), "interior_mid", 0.012 * k)
    box(coll, "AF_RoofLiner", (CX, yt - 0.57 * k, rf - 0.033 * k), (0.676 * Wd, 1.1 * k, 0.018 * k), "interior_mid", 0.006 * k)
    for s in (-1, 1):
        box(coll, f"AF_Visor{s}", (CX + s * 0.158 * Wd, yt - 0.095 * k, rf - 0.072 * k), (0.34 * k, 0.13 * k, 0.018 * k),
            "interior_dark", 0.006 * k, rot_x=-8)
    beam(coll, "AF_RearMirrorStem", Vector((CX, yt + 0.02 * k, rf - 0.05 * k)), Vector((CX, yt + 0.005 * k, rf - 0.09 * k)),
         0.012 * k, 0.012 * k, Vector((0, -1, 0)), "metal", 0.003 * k)
    box(coll, "AF_RearMirror", (CX, yt, rf - 0.112 * k), (0.18 * k, 0.026 * k, 0.052 * k), "interior_dark", 0.008 * k)
    box(coll, "AF_RearMirrorGlass", (CX, yt - 0.019 * k, rf - 0.112 * k), (0.16 * k, 0.004 * k, 0.038 * k), "mirror_glass", 0)

    # side mirror glass + bezel on the car's own mirrors (or a default spot if none were found)
    mw = m["mw"]
    found = {}
    def reflect(v):
        return Vector((2 * CX - v.x, v.y, v.z))

    for side, sd, tag in ((-1, "L", "mirror_left"), (1, "R", "mirror_right")):
        fs = m["mirrors"][side]
        other = m["mirrors"][-side]
        found[sd] = len(fs)
        if fs:
            pts = [mw @ body.data.vertices[vi].co for p, n in fs for vi in p.vertices]
            n = (sum((nn for p, nn in fs), Vector()) / len(fs)).normalized()
            center = sum(pts, Vector()) / len(pts)
        elif other:   # only the other side was found: mirror it across the car
            pts = [reflect(mw @ body.data.vertices[vi].co) for p, nn in other for vi in p.vertices]
            n0 = sum((nn for p, nn in other), Vector()) / len(other)
            n = Vector((-n0.x, n0.y, n0.z)).normalized()
            center = sum(pts, Vector()) / len(pts)
            found[sd] = "mirrored"
        else:
            n = Vector((-side * 0.4, -0.91, -0.09)).normalized()
            center = Vector((CX + side * 0.775 * k, m["y_wb"] - 0.486 * k, m["z_wb"] + 0.028 * k))
            pts = [center]
        up = (Vector((0, 0, 1)) - n * n.z).normalized()
        right = n.cross(up).normalized()
        us = [(p - center).dot(right) for p in pts]
        vs = [(p - center).dot(up) for p in pts]
        ds = [(p - center).dot(n) for p in pts]
        hw = max(0.06 * k, (max(us) - min(us)) / 2 * 0.82)
        hh = max(0.045 * k, (max(vs) - min(vs)) / 2 * 0.82)
        c0 = center + right * (max(us) + min(us)) / 2 + up * (max(vs) + min(vs)) / 2 + n * (max(ds) + 0.004 * k)
        bm = bmesh.new()
        q = [bm.verts.new(c0 + right * a * hw + up * b * hh) for a, b in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        f = bm.faces.new(q)
        f.normal_update()
        if f.normal.dot(n) < 0:
            f.normal_flip()
        add_obj(coll, f"AF_SideMirrorGlass{sd}", bm, tag)
        bm = bmesh.new()
        back = c0 - n * (0.013 * k)
        rings = [[bm.verts.new(back + n * dd + right * a * (hw + 0.008 * k) + up * b * (hh + 0.008 * k))
                  for a, b in ((-1, -1), (1, -1), (1, 1), (-1, 1))] for dd in (0.0, 0.012 * k)]
        a_, b_ = rings
        for i in range(4):
            bm.faces.new((a_[i], a_[(i + 1) % 4], b_[(i + 1) % 4], b_[i]))
        bm.faces.new(a_[::-1])
        bm.faces.new(b_)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        add_obj(coll, f"AF_SideMirrorBezel{sd}", bm, "trim_dark")

    for o in coll.objects:
        if o.type == "MESH" and o.name != "AF_Steerwheel":
            for p in o.data.polygons:
                p.use_smooth = False
    path = export(car + suffix, coll, eye)
    info = {k_: (round(v, 3) if isinstance(v, float) else v) for k_, v in m.items()
            if k_ in ("CX", "W", "k", "roof", "H", "y_wt", "z_wt", "y_wb", "z_wb")}
    info.update(eye_unity=[round(c, 4) for c in u(eye)], pillar_points={s: len(m["edges"][s]) for s in (-1, 1)},
                mirror_faces=found, file=path)
    return info


def export(car, coll, eye):
    dg = bpy.context.evaluated_depsgraph_get()
    lines = [f"# DriverCam cockpit auto-fitted to {car} from the game's prefab (body coordinates, meters).",
             "frame body", f"e {fmt(u(eye))}", "s 0", "w 1.8"]

    def group_of(n):
        if "Shell" in n: return "Shell"
        if "Interior" in n: return "Interior"
        if "Door" in n: return "Doors"
        if "APillar" in n: return "Pillars"
        if "SideMirror" in n: return "MirrorLeft" if n.endswith("L") else "MirrorRight"
        if "RearMirror" in n: return "RearMirror"
        return "Roof"

    for o in sorted(coll.objects, key=lambda o: o.name):
        if o.type != "MESH" or o.name == "AF_Steerwheel":
            continue
        ev = o.evaluated_get(dg)
        me = ev.to_mesh()
        me.calc_loop_triangles()
        mw = o.matrix_world
        nm = mw.to_3x3().inverted_safe().transposed()
        lines += [f"o {o.name}", f"g {group_of(o.name)}"]
        by = {}
        for lt in me.loop_triangles:
            mm = me.materials[lt.material_index] if lt.material_index < len(me.materials) else None
            by.setdefault(tag_for(mm.name if mm else ""), []).append(lt)
        for tag, tris in by.items():
            lines.append(f"m {tag}")
            for lt in tris:
                lines.append("f " + " ".join(
                    fmt(u(mw @ me.vertices[me.loops[li].vertex_index].co)) + " " + fmt(u((nm @ me.corner_normals[li].vector).normalized()))
                    for li in (lt.loops[0], lt.loops[2], lt.loops[1])))
        ev.to_mesh_clear()

    wheel = coll.objects.get("AF_Steerwheel")
    P = np.array([list(v.co) for v in wheel.data.vertices])
    c_b = Vector(P.mean(axis=0))
    ax = Vector(np.linalg.svd(P - P.mean(axis=0))[2][2]).normalized()
    if ax.y < 0:
        ax = -ax
    up_b = (Vector((0, 0, 1)) - ax * ax.z).normalized()
    f_u, up_u = u(ax).normalized(), u(up_b).normalized()
    r_u = up_u.cross(f_u)
    piv = u(c_b)
    lines += ["o SteeringWheel", "g SteeringWheel", f"p {fmt(piv)} {fmt(f_u)} {fmt(up_u)}"]
    me = wheel.data
    me.calc_loop_triangles()

    def loc(v):
        d = u(v) - piv
        return Vector((d.dot(r_u), d.dot(up_u), d.dot(f_u)))

    by = {}
    for lt in me.loop_triangles:
        by.setdefault(tag_for(me.materials[lt.material_index].name if me.materials else ""), []).append(lt)
    for tag, tris in by.items():
        lines.append(f"m {tag}")
        for lt in tris:
            pts = []
            for li in (lt.loops[0], lt.loops[2], lt.loops[1]):
                nw = u(me.corner_normals[li].vector.normalized())
                pts.append(fmt(loc(me.vertices[me.loops[li].vertex_index].co)) + " " + fmt(Vector((nw.dot(r_u), nw.dot(up_u), nw.dot(f_u)))))
            lines.append("f " + " ".join(pts))
    path = os.path.join(OUT, f"cockpit_{car}.dcm")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")
    return path
