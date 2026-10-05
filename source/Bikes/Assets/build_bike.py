"""Builds the Bikes plugin's sport bike models from the downloaded glTF (.glb) files: decimated (rr about 18k triangles with its two-sided copies, lp about 10.8k), the two
wheels split out around their own pivots, written as a .csm with `kind bike` (wheels WheelF / WheelR instead of FL/FR/RL/RR).
blender -b --factory-startup --python build_bike.py -- <rr|lp> <glb> <textures dir> <out dir> [preview prefix]

  rr = BMW S1000RR (~313k tris in loose parts: welded, small parts and unseen faces dropped, decimated per part class,
       faces turned outward (two-sided where seen from both sides), smooth weighted normals; flat colours from a 16-swatch palette texture + see-through glass)
  lp = low-poly sport bike (one textured ~50k-tri triangle soup: welded, wheels cut out by geometry, decimated)
  lp: a new UV atlas, and the source's colours baked onto one base-colour texture (BAKE_PX 2048 px; Cycles, diffuse colour).

Output frame (Unity, metres): +z forward, +y up, +x right; origin on the ground midway between the two axles, on the
centre line. Real size (the bike is NOT scaled to the car: see the bike design doc).
"""
import sys, os, math
import bpy, bmesh
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index("--") + 1:]
KIND, GLB, TEX, OUT = argv[0], argv[1], argv[2], argv[3]
PREVIEW = argv[4] if len(argv) > 4 else None
# triangles: rr 11500 + 2 x 1100 (the cockpit denser, for the rider's view); lp 9000 + 2 x 900 (its blobby surface and round tyres need more to read cleanly)
TARGET_BODY, TARGET_WHEEL = {"rr": (11500, 1100), "lp": (9000, 900)}[KIND]
BAKE_PX = {"rr": 1024, "lp": 2048}[KIND]        # base-colour texture size
NAME = {"rr": "BMW_S1000RR", "lp": "SportBike"}[KIND]
LENGTH_LP = 2.07                               # the low-poly bike's real length (m): an R1-class sport bike

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=GLB)

def sel_only(objs, active=None):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = active or objs[0]

def tri_count(o): return sum(len(p.vertices) - 2 for p in o.data.polygons)

def decimate(o, target):
    n = tri_count(o)
    if n <= target: return
    m = o.modifiers.new("dec", 'DECIMATE'); m.decimate_type = 'COLLAPSE'; m.ratio = max(0.0005, target / n)
    m.use_collapse_triangulate = True
    sel_only([o]); bpy.ops.object.modifier_apply(modifier=m.name)

def join(objs):
    objs = [o for o in objs if o is not None]
    if len(objs) == 1: return objs[0]
    sel_only(objs); bpy.ops.object.join(); return bpy.context.view_layer.objects.active

def bounds(objs):
    ws = [o.matrix_world @ v.co for o in objs for v in o.data.vertices]
    mn = Vector((min(v.x for v in ws), min(v.y for v in ws), min(v.z for v in ws)))
    mx = Vector((max(v.x for v in ws), max(v.y for v in ws), max(v.z for v in ws)))
    return mn, mx

# flatten: meshes only, parents cleared (world transform kept), transforms applied
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH' and o.name != "Icosphere"]
for o in list(bpy.context.scene.objects):
    if o not in meshes: bpy.data.objects.remove(o, do_unlink=True)
sel_only(meshes); bpy.ops.object.parent_clear(type='CLEAR_KEEP_TRANSFORM')
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

def lin2srgb(x): return 12.92 * x if x <= 0.0031308 else 1.055 * x ** (1 / 2.4) - 0.055

if KIND == "rr":
    # The source's base-colour slots hold a mud overlay (its real colours live in alpha-only maps), so baking them gave
    # a green, blotchy bike. Instead every source material is mapped to a part class with a flat colour from a small
    # palette texture (one swatch per class), in three materials (paint / trim / metal), plus see-through glass. The source is hundreds of
    # loose single-sided shells: they are welded, bolts dropped, faces no camera can see deleted, then decimated
    # (collapse); rr_finish() turns every face outward, makes faces seen from both sides two-sided and sets
    # weighted smooth normals with hard edges.
    from mathutils.bvhtree import BVHTree
    DROP = {"1", "2", "3", "4", "5", "6", "8", "9", "N", "dials", "takometro", "LBUTTONS", "ign", "interior", "cockpit"}
    for o in list(meshes):
        if o.name in DROP: meshes.remove(o); bpy.data.objects.remove(o, do_unlink=True)
    RR_CLASS = {   # class: (palette swatch, tag, sRGB colour)
        "white": (0, "paint", (236, 236, 238)), "blue": (1, "paint", (18, 72, 168)), "red": (2, "paint", (196, 22, 32)),
        "gblack": (3, "paint", (20, 20, 22)), "glass": (4, "glass", (34, 40, 48)), "lamp": (5, "paint", (232, 238, 244)),
        "tail": (6, "paint", (178, 10, 12)), "chrome": (7, "metal", (200, 202, 206)), "black": (8, "trim", (30, 30, 32)),
        "tyre": (9, "trim", (24, 24, 24)), "seat": (10, "trim", (40, 40, 42)), "rim": (11, "trim", (36, 36, 40)),
        "disc": (12, "trim", (128, 128, 132)), "engine": (13, "metal", (72, 74, 78)), "alu": (14, "metal", (160, 163, 168)),
        "exhaust": (15, "metal", (105, 102, 98))}
    def rr_class(ob, mat):
        if "decal_logo" in mat or "vehicle_badges" in mat: return None      # decals floating on the paint
        if ob.startswith("wheel_"): return "tyre" if "tyres" in mat else "rim"
        if ob.startswith("bikedisc"): return "disc" if "brakes" in mat else "rim"
        if ob == "bodyshell":
            for k, c in (("[PRIMARY]", "white"), ("vehicle_paint1", "white"), ("[SECONDARY]", "gblack"), ("carbon fiber red", "gblack"),
                         ("carbonfiber_001", "blue"), ("plastic_d8", "red"), ("gold-texture", "chrome"), ("plastic", "black")):
                if k in mat: return c
        if ob == "chassis": return "glass" if "glass" in mat else "engine"
        if ob == "engineblock": return "exhaust"
        if ob == "extra_7": return "blue"
        if ob == "forks_l": return "blue" if "carbon" in mat else ("white" if "livery" in mat else "chrome")
        if ob in ("forks_u", "rear-frame", "swingarm"): return "engine" if "chain" in mat else "alu"
        if ob == "rear-seat": return "white"
        if ob == "seat": return "seat"
        if ob == "brakelight_l": return "tail"
        if ob.startswith("headlight"): return "gblack" if "carbon" in mat else "lamp"
        if ob == "handlebars":
            if "glass" in mat: return "gblack"       # the dash display: dark gloss (its internals are dropped)
            if "braided" in mat: return None          # brake lines and cables: thin tubes turn into shards
            if "gold" in mat: return "chrome"
        return "black"
    rr_mats = {c: bpy.data.materials.new("rr_" + c) for c in RR_CLASS}
    for o in meshes:
        me = o.data
        slot = [rr_class(o.name, m.name if m else "") for m in me.materials] or [rr_class(o.name, "")]
        def cl(f): return slot[min(f.material_index, len(slot) - 1)]
        bm = bmesh.new(); bm.from_mesh(me)
        # brake discs and the sprocket are flat plates with their holes in the texture's alpha: cut those faces out
        cut = {}
        for i, m in enumerate(me.materials):
            for nd in (m.node_tree.nodes if m and m.use_nodes else []):
                im = getattr(nd, "image", None)
                if im and ("brakes" in im.name or "gear" in im.name) and im.channels == 4 and im.size[0] > 0:
                    cut[i] = (im.size[0], im.size[1], im.pixels[:])
        uvl = bm.loops.layers.uv.active
        def holed(f):
            if f.material_index not in cut or uvl is None: return False
            w, h, px = cut[f.material_index]
            u = sum(l[uvl].uv.x for l in f.loops) / len(f.loops) % 1.0; v = sum(l[uvl].uv.y for l in f.loops) / len(f.loops) % 1.0
            return px[(int(v * h) * w + int(u * w)) * 4 + 3] < 0.5
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if cl(f) is None or holed(f)], context='FACES')
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0002)        # glTF splits vertices at every UV / normal seam
        # where welding joined more than two faces on an edge (touching parts), split it again: the decimator stalls there
        bmesh.ops.split_edges(bm, edges=[e for e in bm.edges if len(e.link_faces) > 2])
        # loose shells smaller than 20 mm (bolts, clips; 50 mm on the bars: buttons, switch parts) only make shards here: drop them
        bm.faces.ensure_lookup_table(); seen = set(); small = []
        for f in bm.faces:
            if f.index in seen: continue
            stack = [f]; comp = []; seen.add(f.index)
            while stack:
                g = stack.pop(); comp.append(g)
                for e in g.edges:
                    for h in e.link_faces:
                        if h.index not in seen: seen.add(h.index); stack.append(h)
            vs = {v for g in comp for v in g.verts}
            mn = Vector((min(v.co.x for v in vs), min(v.co.y for v in vs), min(v.co.z for v in vs)))
            mx = Vector((max(v.co.x for v in vs), max(v.co.y for v in vs), max(v.co.z for v in vs)))
            if (mx - mn).length < (0.05 if o.name == "handlebars" else 0.02): small.extend(comp)
        bmesh.ops.delete(bm, geom=small, context='FACES')
        names = [cl(f) for f in bm.faces]
        bm.to_mesh(me); bm.free()
        me.materials.clear()
        order = sorted(set(names))
        for c in order: me.materials.append(rr_mats[c])
        me.polygons.foreach_set("material_index", [order.index(c) for c in names])
        me.update()
    byname = {o.name: o for o in meshes}
    WHEEL_PARTS = ("wheel_lf.child", "wheel_lr.child", "bikedisc_f", "bikedisc_r")
    rest = [o for o in meshes if o.name not in WHEEL_PARTS]
    wheelF = join([byname["wheel_lf.child"], byname["bikedisc_f"]])
    wheelR = join([byname["wheel_lr.child"], byname["bikedisc_r"]])
    body = join(rest)
    def rr_dirs(n=64):
        out = []
        for i in range(n):      # Fibonacci sphere, minus straight up from under the road
            z = 1 - 2 * (i + 0.5) / n; r = math.sqrt(1 - z * z); a = i * math.pi * (3 - math.sqrt(5))
            if z > -0.75: out.append(Vector((r * math.cos(a), r * math.sin(a), z)))
        return out
    RR_DIRS = rr_dirs()
    def rr_bvh(objs):
        """Ray tree of the solid parts: the see-through windscreen does not hide what is behind it."""
        vs, ps = [], []
        for o in objs:
            glass = {i for i, m in enumerate(o.data.materials) if m and m.name == "rr_glass"}
            b = len(vs); vs += [v.co.copy() for v in o.data.vertices]
            ps += [[b + i for i in p.vertices] for p in o.data.polygons if p.material_index not in glass]
        return BVHTree.FromPolygons(vs, ps)
    def rr_seen(tree, c, n, d):
        s = 1.0 if n.dot(d) >= 0 else -1.0
        return tree.ray_cast(c + n * (s * 0.0003) + d * 0.0003, d, 6.0)[0] is None
    # delete faces no camera can see (engine internals, the inner skins of double-walled panels)
    tree = rr_bvh([body, wheelF, wheelR])
    for o in (body, wheelF, wheelR):
        bm = bmesh.new(); bm.from_mesh(o.data); hidden = []
        for f in bm.faces:
            c, n = f.calc_center_median(), f.normal
            if n.length < 0.5: continue
            if not (rr_seen(tree, c, n, n) or rr_seen(tree, c, n, -n) or any(rr_seen(tree, c, n, d) for d in RR_DIRS)): hidden.append(f)
        print(f"[bike] {o.name}: {len(hidden)} of {len(bm.faces)} faces hidden, deleted")
        bmesh.ops.delete(bm, geom=hidden, context='FACES'); bm.to_mesh(o.data); bm.free()
    def rr_decimate(o, target):
        """Collapse-decimate each class on its own (so colour borders stay clean instead of zig-zagging), budget by
        sqrt(triangles x area) with more for the screen and the paint, then weld the borders back together."""
        sel_only([o]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.mesh.separate(type='MATERIAL'); bpy.ops.object.mode_set(mode='OBJECT')
        pieces = list(bpy.context.selected_objects)
        if o.name == "bodyshell":
            # the cockpit (bars, yoke, dash, inner fairing) is seen up close from the rider's eye: its own pieces, 3.5x denser
            for p in list(pieces):
                bm = bmesh.new(); bm.from_mesh(p.data); k = 0
                for f in bm.faces:
                    c = f.calc_center_median(); f.select = abs(c.x) < 0.45 and 0.15 < c.y < 0.75 and c.z > 0.22; k += f.select
                for v in bm.verts: v.select = False
                for e in bm.edges: e.select = False
                bm.to_mesh(p.data); bm.free()
                if 0 < k < len(p.data.polygons):
                    sel_only([p]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_mode(type='FACE')
                    bpy.ops.mesh.separate(type='SELECTED'); bpy.ops.object.mode_set(mode='OBJECT')
                    q = [x for x in bpy.context.selected_objects if x != p][0]; q["cockpit"] = 1; pieces.append(q)
                elif k: p["cockpit"] = 1
        BOOST = {"rr_glass": 1.5, "rr_white": 1.6, "rr_blue": 1.6, "rr_red": 1.6, "rr_tyre": 1.4, "rr_disc": 3.0, "rr_exhaust": 1.3,
                 "rr_engine": 0.8, "rr_black": 0.8}
        def cname(p): return p.data.materials[p.data.polygons[0].material_index].name if p.data.polygons else "-"
        w = {p: (3.5 if p.get("cockpit") else 1.0) * BOOST.get(cname(p), 1.0) * math.sqrt(tri_count(p) * max(sum(f.area for f in p.data.polygons), 1e-5)) for p in pieces}
        for p in pieces:
            t = max(24, int(target * w[p] / sum(w.values()))); n = tri_count(p)
            if n > t:
                m = p.modifiers.new("dec", 'DECIMATE'); m.decimate_type = 'COLLAPSE'; m.ratio = t / n
                m.use_collapse_triangulate = True
                sel_only([p]); bpy.ops.object.modifier_apply(modifier=m.name)
            print(f"[bike]   {o.name} {cname(p)}{' (cockpit)' if p.get('cockpit') else ''}: {n} -> {tri_count(p)} (budget {t})")
        o = join(pieces)
        bm = bmesh.new(); bm.from_mesh(o.data); bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0006); bm.to_mesh(o.data); bm.free()
        return o
    body = rr_decimate(body, TARGET_BODY); wheelF = rr_decimate(wheelF, TARGET_WHEEL); wheelR = rr_decimate(wheelR, TARGET_WHEEL)
    orig = None
    # paint / trim / metal take their colours from the palette; the windscreen is see-through smoked glass (alpha)
    TAGS = {"paint": ((1.0, 1.0, 1.0), 0.75, 0.0, 0.0), "trim": ((1.0, 1.0, 1.0), 0.25, 0.0, 0.0),
            "metal": ((1.0, 1.0, 1.0), 0.6, 0.5, 0.0), "glass": ((0.016, 0.021, 0.029), 0.9, 0.0, 0.0, 0.4)}
else:
    obj = meshes[0]
    # forward is -x: turn so it is +y, then scale to real length
    obj.data.transform(Matrix.Rotation(math.radians(-90), 4, 'Z'))
    mn, mx = bounds([obj]); scale = LENGTH_LP / (mx.y - mn.y)
    obj.data.transform(Matrix.Scale(scale, 4))
    mn, mx = bounds([obj])
    # the source is a triangle soup with a fragmented UV atlas: decimating it scrambles the UVs. Keep an untouched copy
    # to bake the colours from, weld the working mesh, decimate, unwrap again and bake (below).
    orig = obj.copy(); orig.data = obj.data.copy(); bpy.context.scene.collection.objects.link(orig)
    sel_only([obj]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.remove_doubles(threshold=0.002); bpy.ops.object.mode_set(mode='OBJECT')
    # wheels: find each axle by fitting the tyre tread (vertices near the centre plane, in the front / rear third)
    vs = [v.co.copy() for v in obj.data.vertices]
    def fit(front):
        best = None
        for r10 in range(240, 360, 4):               # radius 0.24-0.36 m
            r = r10 / 1000.0
            cy = (mx.y - r) if front else (mn.y + r); cz = mn.z + r
            n = 0
            for v in vs:
                if abs(v.x) < 0.05 and abs(math.hypot(v.y - cy, v.z - cz) - r) < 0.012: n += 1
            if best is None or n > best[0]: best = (n, r, cy, cz)
        return best
    fF, fR = fit(True), fit(False)
    print(f"[bike] axle fit front r={fF[1]:.3f} at y={fF[2]:.3f} z={fF[3]:.3f} ({fF[0]} tread verts); rear r={fR[1]:.3f} at y={fR[2]:.3f} z={fR[3]:.3f} ({fR[0]})")
    def cut(fit_, half_width):
        _, r, cy, cz = fit_
        bm = bmesh.new(); bm.from_mesh(obj.data)
        for f in bm.faces:
            f.select = all(abs(v.co.x) < half_width and math.hypot(v.co.y - cy, v.co.z - cz) < r * 1.01 for v in f.verts)
        bm.to_mesh(obj.data); bm.free()
        sel_only([obj]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.separate(type='SELECTED'); bpy.ops.object.mode_set(mode='OBJECT')
        return [o for o in bpy.context.selected_objects if o != obj][0]
    wheelF = cut(fF, 0.075)                           # front tyre ~120 mm, rim + disc inside
    wheelR = cut(fR, 0.105)                           # rear tyre ~190 mm
    body = obj
    TAGS = {"paint": ((1.0, 1.0, 1.0), 0.5, 0.2, 0.0)}
    decimate(body, TARGET_BODY)
    fwd_rot = Matrix.Identity(4)

decimate(wheelF, TARGET_WHEEL); decimate(wheelR, TARGET_WHEEL)
if KIND == "lp":
    # smooth shading with hard edges past 40 degrees (the welded soup otherwise shades blotchy)
    for o in (body, wheelF, wheelR):
        sel_only([o]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.mesh.dissolve_degenerate(threshold=0.0002); bpy.ops.object.mode_set(mode='OBJECT')
        bpy.ops.object.shade_smooth_by_angle(angle=math.radians(40))

bake_img = None
if orig is not None:
    # one new UV atlas over body + both wheels, then the source colours baked onto it (Cycles, diffuse colour only)
    parts = [body, wheelF, wheelR]
    for o in parts:
        o.data.materials.clear()
        while o.data.uv_layers: o.data.uv_layers.remove(o.data.uv_layers[0])
        o.data.uv_layers.new(name="bake")
    sel_only(parts); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.004); bpy.ops.object.mode_set(mode='OBJECT')
    bake_img = bpy.data.images.new(NAME + "_bake", BAKE_PX, BAKE_PX)
    bm_ = bpy.data.materials.new("bake"); bm_.use_nodes = True
    tn = bm_.node_tree.nodes.new("ShaderNodeTexImage"); tn.image = bake_img; bm_.node_tree.nodes.active = tn
    for o in parts: o.data.materials.append(bm_)
    sc = bpy.context.scene; sc.render.engine = 'CYCLES'; sc.cycles.samples = 4; sc.cycles.device = 'CPU'
    bk = sc.render.bake; bk.use_selected_to_active = True; bk.cage_extrusion = 0.02; bk.max_ray_distance = 0.08
    bk.use_pass_direct = False; bk.use_pass_indirect = False; bk.use_pass_color = True
    for i, o in enumerate(parts):
        bk.use_clear = (i == 0)
        sel_only([orig, o], active=o)
        bpy.ops.object.bake(type='DIFFUSE', pass_filter={'COLOR'}, use_clear=(i == 0), margin=4)
    bpy.data.objects.remove(orig, do_unlink=True)

if KIND == "rr":
    def rr_finish():
        """Faces outward (by the side cameras and the rider's eye see), faces seen from both sides two-sided, smooth weighted normals with hard
        edges past 35 degrees and between classes, palette UVs. Returns the palette image."""
        tree = rr_bvh([body, wheelF, wheelR])
        # the rider's eye (DriverCam's bike view, upright to tucked): the far-away directions above miss what only the rider
        # sees from inside the fairing (the screen's inside, inner fairing, clocks), so those faces were kept one-sided
        # facing out and showed as holes from the seat (URP Lit culls back faces). Eye points in the output frame (ground,
        # midway between the axles), moved here into the source frame by the same origin as below.
        cF, cR = [sum(bounds([w]), Vector()) / 2 for w in (wheelF, wheelR)]
        o0 = Vector((0.0, (cF.y + cR.y) / 2, bounds([body, wheelF, wheelR])[0].z))
        EYES = [o0 + Vector((x, y, z)) for x in (-0.12, 0.0, 0.12) for y in (-0.05, 0.1, 0.25) for z in (1.15, 1.25, 1.35, 1.45)]
        def eye_sides(pts, n):
            """(front seen, back seen) by any of the eye points from any sample point, with nothing in between."""
            sf = sb = False
            for e in EYES:
                for c in pts:
                    d = e - c; L = d.length; d = d / L; k = n.dot(d)
                    if abs(k) < 0.02 or (sf if k > 0 else sb): continue
                    s = 1.0 if k > 0 else -1.0
                    if tree.ray_cast(c + n * (s * 0.0003) + d * 0.0003, d, L - 0.0006)[0] is None:
                        if s > 0: sf = True
                        else: sb = True
                        if sf and sb: return sf, sb
            return sf, sb
        for o in (body, wheelF, wheelR):
            bm = bmesh.new(); bm.from_mesh(o.data)
            bmesh.ops.dissolve_degenerate(bm, dist=0.0002, edges=bm.edges)
            hidden, flip, both = [], [], []
            eyeb = 0
            for f in bm.faces:
                c, n = f.calc_center_median(), f.normal
                if n.length < 0.5: hidden.append(f); continue
                # the centre and points near each corner: a face partly behind another part still shows its open corner
                pts = [c] + [c.lerp(v.co, 0.7) for v in f.verts]
                fr = bk = 0.0
                for d in RR_DIRS:
                    if any(rr_seen(tree, q, n, d) for q in pts):
                        k = n.dot(d)
                        if k > 0: fr += k
                        else: bk -= k
                ef, eb = eye_sides(pts, n)
                if fr == 0 and bk == 0:
                    if not (ef or eb): hidden.append(f); continue
                    flipped = eb and not ef         # only the rider sees it: face it at the rider
                else: flipped = bk > fr
                if flipped: flip.append(f)
                if min(fr, bk) > 0.35 * max(fr, bk) and min(fr, bk) > 1.0: both.append(f)
                elif (ef if flipped else eb): both.append(f); eyeb += 1   # the rider sees its back: two-sided
            bmesh.ops.reverse_faces(bm, faces=flip)
            bmesh.ops.delete(bm, geom=hidden, context='FACES')
            if both:
                dup = bmesh.ops.duplicate(bm, geom=both)
                bmesh.ops.reverse_faces(bm, faces=[g for g in dup["geom"] if isinstance(g, bmesh.types.BMFace)])
            print(f"[bike] {o.name}: {len(flip)} faces turned outward, {len(both)} made two-sided ({eyeb} for the rider's eye), {len(hidden)} unseen dropped")
            bm.to_mesh(o.data); bm.free()
            me = o.data
            me.shade_smooth(); me.set_sharp_from_angle(angle=math.radians(35))
            sharp = me.attributes.get("sharp_edge") or me.attributes.new("sharp_edge", 'BOOLEAN', 'EDGE')
            emat = {}
            for p in me.polygons:
                for ek in p.edge_keys: emat.setdefault(ek, set()).add(p.material_index)
            for e in me.edges:
                if len(emat.get(e.key, ())) > 1: sharp.data[e.index].value = True
            m = o.modifiers.new("wn", 'WEIGHTED_NORMAL'); m.keep_sharp = True; m.weight = 50; m.mode = 'FACE_AREA'
            sel_only([o]); bpy.ops.object.modifier_apply(modifier=m.name)
            uvl = me.uv_layers.new(name="pal"); cls = [mt.name[3:] for mt in me.materials]
            for p in me.polygons:
                i = RR_CLASS[cls[p.material_index]][0]; uv = ((i % 4 + 0.5) / 4, (i // 4 + 0.5) / 4)
                for li in p.loop_indices: uvl.data[li].uv = uv
            me.uv_layers.active = uvl
        img = bpy.data.images.new(NAME + "_palette", 256, 256)
        px = [0.0] * (256 * 256 * 4)
        for c, (i, _, rgb) in RR_CLASS.items():
            x0, y0 = (i % 4) * 64, (i // 4) * 64
            row = [rgb[0] / 255, rgb[1] / 255, rgb[2] / 255, 1.0] * 64
            for y in range(y0, y0 + 64):
                k = (y * 256 + x0) * 4; px[k:k + 256] = row
        img.pixels = px
        return img
    bake_img = rr_finish()

# pivots: wheel bounding-box centres; frame origin: ground, midway between the axles, on the centre line
def centre(o):
    mn, mx = bounds([o]); return (mn + mx) / 2
pF, pR = centre(wheelF), centre(wheelR)
pF.x = pR.x = 0.0
mnAll, _ = bounds([body, wheelF, wheelR])
origin = Vector((0.0, (pF.y + pR.y) / 2, mnAll.z))
for o in (body, wheelF, wheelR): o.data.transform(Matrix.Translation(-origin))
pF -= origin; pR -= origin

os.makedirs(OUT, exist_ok=True)
texfile = None
if bake_img is not None:
    img = bake_img
    texfile = NAME + "_basecolor.jpg"
    img.filepath_raw = os.path.join(OUT, texfile); img.file_format = 'JPEG'
    bpy.context.scene.render.image_settings.quality = 88
    img.save()

def fmt(v): return " ".join(f"{c:.5f}" for c in v)

def write_part(lines, name, o, tagnames, pivot=None):
    """Unity frame: (x, z, y), winding reversed (loops 0, 2, 1); wheel triangles relative to their pivot."""
    me = o.data; me.calc_loop_triangles()
    uv = me.uv_layers.active.data if (texfile and me.uv_layers) else None
    lines.append(f"o {name}")
    if pivot is not None: lines.append(f"p {pivot.x:.5f} {pivot.z:.5f} {pivot.y:.5f}")
    by_tag = {}
    for lt in me.loop_triangles:
        t = tagnames(me, lt.material_index)
        by_tag.setdefault(t, []).append(lt)
    n = 0
    for t, lts in by_tag.items():
        lines.append(f"m {t}")
        for lt in lts:
            pts = []
            for li in (lt.loops[0], lt.loops[2], lt.loops[1]):
                v = me.vertices[me.loops[li].vertex_index].co.copy()
                if pivot is not None: v = v - pivot
                nn = me.corner_normals[li].vector.normalized() if hasattr(me, "corner_normals") else me.vertices[me.loops[li].vertex_index].normal
                s = fmt((v.x, v.z, v.y)) + " " + fmt((nn.x, nn.z, nn.y))
                if uv is not None: s += f" {uv[li].uv.x:.5f} {uv[li].uv.y:.5f}"
                pts.append(s)
            lines.append(("u " if uv is not None else "f ") + " ".join(pts)); n += 1
    return n

def tag_name(me, i):
    if KIND == "rr": return RR_CLASS[me.materials[i].name[3:]][1]
    if texfile: return "paint"
    m = me.materials[i] if i < len(me.materials) else None
    return m.name[4:] if m and m.name.startswith("tag_") else "c0"

src = {"rr": "BMW S1000 RR by VTX, Sketchfab, CC BY-NC-SA 4.0", "lp": "Low Poly Motorcycle by pyrzegeclb, Sketchfab, CC BY 4.0"}[KIND]
lines = [f"# CarSkins model '{NAME}' ({src}; decimated by build_bike.py; Unity axes, metres, real size)",
         "kind bike", f"name {NAME}"]
if texfile: lines += [f"tex {t} {texfile}" for t in (("paint", "trim", "metal") if KIND == "rr" else ["paint"])]
for t, (c, s, met, e, *alpha) in TAGS.items():   # an optional alpha under 1 = see-through glass (no texture)
    lines.append(f"mat {t} {lin2srgb(c[0]):.4f} {lin2srgb(c[1]):.4f} {lin2srgb(c[2]):.4f} {s:.4f} {met:.4f} {e:.4f}" + "".join(f" {a:.4f}" for a in alpha))
tris = write_part(lines, "Body", body, tag_name)
tris += write_part(lines, "WheelF", wheelF, tag_name, pF)
tris += write_part(lines, "WheelR", wheelR, tag_name, pR)
open(os.path.join(OUT, NAME + ".csm"), "w", encoding="utf-8").write("\n".join(lines) + "\n")
mnB, mxB = bounds([body, wheelF, wheelR])
print(f"[bike] {NAME}: {tris} triangles (body {tri_count(body)}, wheels {tri_count(wheelF)} + {tri_count(wheelR)}); "
      f"size {mxB.x - mnB.x:.2f} x {mxB.z - mnB.z:.2f} x {mxB.y - mnB.y:.2f} m (w x h x l); wheelbase {pF.y - pR.y:.3f} m; "
      f"wheel radius F {pF.z:.3f} R {pR.z:.3f}")

if PREVIEW:
    sc = bpy.context.scene
    def flat(c, e=0.0):
        m = bpy.data.materials.new("p"); m.use_nodes = True; b = m.node_tree.nodes["Principled BSDF"]
        b.inputs["Base Color"].default_value = (c[0], c[1], c[2], 1); return m
    if texfile:
        sc.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE_NEXT"
        mat = bpy.data.materials.new("t"); mat.use_nodes = True
        bsdf = mat.node_tree.nodes["Principled BSDF"]; tn = mat.node_tree.nodes.new("ShaderNodeTexImage"); tn.image = img
        mat.node_tree.links.new(tn.outputs["Color"], bsdf.inputs["Base Color"])
        for o in (body, wheelF, wheelR): o.data.materials.clear(); o.data.materials.append(mat)
    else:
        for t, (c, s, met, e) in TAGS.items():
            m = bpy.data.materials.get("tag_" + t)
            if m is None: continue
            m.use_nodes = True; m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (c[0], c[1], c[2], 1)
    for o in (wheelF, wheelR): o.location = o.location   # pivots are only metadata here
    w = bpy.data.worlds.new("W"); sc.world = w; w.use_nodes = True; w.node_tree.nodes["Background"].inputs[0].default_value = (0.45, 0.48, 0.52, 1)
    sun = bpy.data.objects.new("S", bpy.data.lights.new("S", "SUN")); sc.collection.objects.link(sun); sun.rotation_euler = (0.8, 0.1, 0.7); sun.data.energy = 3
    sc.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE_NEXT"
    sc.render.resolution_x, sc.render.resolution_y = 960, 540
    cd = bpy.data.cameras.new("C"); cam = bpy.data.objects.new("C", cd); sc.collection.objects.link(cam); sc.camera = cam
    for nm, loc in (("q", (3.2, 2.6, 1.3)), ("side", (4.2, 0.0, 0.6))):
        cam.location = loc; cam.rotation_euler = (Vector((0, 0, 0.55)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        sc.render.filepath = f"{PREVIEW}_{nm}.png"; bpy.ops.render.render(write_still=True)
