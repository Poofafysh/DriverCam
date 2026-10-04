"""Shared helpers for DriverCam cockpit (.dcm) files inside Blender.

Unity body frame (x right, y up, z forward, left-handed) <-> Blender (x right, y forward, z up): swap y and z.
Triangles: build_interior.py writes each Blender triangle as corners (0, 2, 1) with y and z swapped, and that is the
game's front face. to_blender() reads them back in (0, 2, 1) order, so a Blender face's front is the game's front, and
render() culls back faces like the game (URP Lit, _Cull = 2): a surface missing in game is missing in the preview too.
"""
import bpy, bmesh, math, os
from mathutils import Vector

PALETTE = {  # preview colours only (the game uses Cockpit.TagMaterial or the model's own `mat` lines)
    "interior_dark": (0.07, 0.06, 0.14), "interior_mid": (0.15, 0.12, 0.26), "trim_dark": (0.1, 0.08, 0.19),
    "paint": (0.6, 0.1, 0.1), "metal": (0.62, 0.66, 0.8), "glass": (0.25, 0.35, 0.6), "mirror_glass": (0.6, 0.7, 0.8),
    "mirror_left": (0.6, 0.7, 0.8), "mirror_right": (0.6, 0.7, 0.8), "shell": (0.3, 0.3, 0.32),
}


def u2b(x, y, z): return Vector((x, z, y))
def b2u(v): return (v.x, v.z, v.y)


def parse(path):
    """-> dict(header lines list, parts list of dict(name, group, pivot or None, tags {tag: [(v,n,uv)*3 per tri]}, gauge [lines]
    for gauge parts only), mats {tag: tuple})"""
    model = {"header": [], "parts": [], "mats": {}, "eye": None, "tex": {}}
    part = None; tag = "interior_dark"
    for raw in open(path, encoding="utf-8"):
        t = raw.split()
        if not t or t[0].startswith("#"): continue
        k = t[0]
        if k in ("s", "w", "frame", "e", "tex", "layout"):
            model["header"].append(raw.rstrip("\n"))
            if k == "e": model["eye"] = tuple(float(x) for x in t[1:4])
            if k == "tex": model["tex"][t[1]] = t[2]
        elif k == "mat":
            model["mats"][t[1]] = tuple(float(x) for x in t[2:8])
        elif k == "o":
            part = {"name": t[1], "group": "Misc", "pivot": None, "tags": {}}; model["parts"].append(part); tag = "interior_dark"
        elif k == "p":
            f = [float(x) for x in t[1:10]]; part["pivot"] = (tuple(f[0:3]), tuple(f[3:6]), tuple(f[6:9]))
        elif k in ("n", "dg", "db"):   # working-gauge lines (needle range, digits, bars): kept raw, in order
            part.setdefault("gauge", []).append(raw.strip())
        elif k == "g": part["group"] = t[1]
        elif k == "m": tag = t[1]
        elif k in ("f", "u"):
            st = 8 if k == "u" else 6
            tri = []
            for i in range(3):
                b = 1 + i * st
                v = tuple(float(x) for x in t[b:b + 3]); n = tuple(float(x) for x in t[b + 3:b + 6])
                uv = (float(t[b + 6]), float(t[b + 7])) if k == "u" else None
                tri.append((v, n, uv))
            part["tags"].setdefault(tag, []).append(tri)
    return model


def material(tag, color=None):
    m = bpy.data.materials.get("dcm_" + tag) or bpy.data.materials.new("dcm_" + tag)
    c = color or PALETTE.get(tag, (0.4, 0.4, 0.4))
    m.diffuse_color = (c[0], c[1], c[2], 1.0)
    return m


def to_blender(model, coll, skip_groups=(), mats=None):
    """Creates one object per part (pivot parts are placed in the body frame too)."""
    objs = []
    for part in model["parts"]:
        if part["group"] in skip_groups: continue
        me = bpy.data.meshes.new(part["name"]); bm = bmesh.new()
        slots = []
        for tag, tris in part["tags"].items():
            slots.append(tag); si = len(slots) - 1
            for tri in tris:
                vs = []
                for (v, n, uv) in tri:
                    if part["pivot"] is not None:   # pivot parts are stored in pivot space: right=x, up=y, forward=z
                        P, F, U = (Vector(part["pivot"][0]), Vector(part["pivot"][1]).normalized(), Vector(part["pivot"][2]).normalized())
                        R = F.cross(U) * -1  # Unity LookRotation: right = up x forward
                        R = U.cross(F)
                        w = P + R * v[0] + U * v[1] + F * v[2]
                        vs.append(bm.verts.new(u2b(*w)))
                    else:
                        vs.append(bm.verts.new(u2b(*v)))
                vs = [vs[0], vs[2], vs[1]]   # undo the file's (0, 2, 1) corner order: Blender front = game front
                try:
                    f = bm.faces.new(vs); f.material_index = si
                except ValueError:
                    pass
        bm.to_mesh(me); bm.free()
        for tag in slots: me.materials.append(material(tag, (mats or {}).get(tag)))
        ob = bpy.data.objects.new(part["name"], me); coll.objects.link(ob); objs.append(ob)
    return objs


def camera_at_eye(eye, fov_deg=78, yaw=0.0, pitch=-6.0, vertical=False):
    cam = bpy.data.objects.get("EyeCam")
    if cam is None:
        cd = bpy.data.cameras.new("EyeCam"); cam = bpy.data.objects.new("EyeCam", cd); bpy.context.scene.collection.objects.link(cam)
    cam.data.lens_unit = "FOV"; cam.data.sensor_fit = "VERTICAL" if vertical else "AUTO"; cam.data.angle = math.radians(fov_deg); cam.data.clip_start = 0.02
    cam.location = u2b(*eye)
    # Blender camera looks down -Z; rotate to look along +Y (forward), then yaw/pitch
    cam.rotation_euler = (math.radians(90 + pitch), 0.0, math.radians(-yaw))
    bpy.context.scene.camera = cam
    return cam


def render(path, w=1280, h=720):
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_WORKBENCH"
    sc.display.shading.light = "STUDIO"
    sc.display.shading.color_type = "MATERIAL"
    sc.display.shading.show_cavity = True
    sc.display.shading.show_object_outline = True
    sc.display.shading.show_backface_culling = True   # the game culls back faces: holes show as holes
    sc.render.resolution_x, sc.render.resolution_y = w, h
    sc.render.film_transparent = False
    sc.world = sc.world or bpy.data.worlds.new("W")
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)


def clear_scene():
    for o in list(bpy.data.objects): bpy.data.objects.remove(o, do_unlink=True)
    for m in list(bpy.data.meshes): bpy.data.meshes.remove(m)
