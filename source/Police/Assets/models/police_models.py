"""Original police car exteriors for the Police plugin, built procedurally in Blender (no third-party or game meshes).

blender -b --factory-startup --python police_models.py -- <out dir> [preview dir]

Three designs: Interceptor (full-size sedan), Pursuit (long-hood fastback coupe), Utility (SUV). Each body is a loft of
rounded cross-sections along the car (wheel arches cut by the stations, wheel-well liners behind them), a greenhouse loft
(raked windscreen and rear glass, painted roof), pillars, push bar, bumpers, lights, mirrors, spotlight, 'POLICE' door
decals and four wheels with their own pivots. Livery tags: paint_a (black: nose, tail, wings) and paint_b (white:
doors, roof), so the plugin can recolour them.

Output: <Name>.pcm, a small text format (Unity axes x right, y up, z forward, metres, origin on the ground at the car's
centre): `name`, `roof x y z` (where the lightbar goes), `tex <tag> <png>`, `mat <tag> r g b smoothness metallic
emission`, then parts: `o <part>`, `p x y z` (wheel pivot: spins about its local x), `v x y z nx ny nz [u v]` (the part's
welded vertices: one per position + normal + uv within a tag), `m <tag>`, `t a b c` (triangles: indices into the part's
`v` list, already in Unity's winding). The plugin also still reads the older unwelded `f`/`u` triangle lines.

Triangle budget (Police 0.6.0 perf pass): patrols are mostly seen at 20-300 m, so loft stations that add nothing there
are dropped (decimate(): a station goes only if every point of its section is within LOFT_TOL of the straight line
between its kept neighbours and no paint / glass / light boundary sits on it, so the silhouette and the livery stay),
the mirror pods are chamfered instead of rounded, the bumpers and thin bars have fewer segments, and the caps that are
always hidden (the rim's and hub's inner faces, inside the tyre) are left out.
"""
import sys, os, math
import bpy, bmesh
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index("--") + 1:]
OUT = os.path.abspath(argv[0])
PREVIEW = os.path.abspath(argv[1]) if len(argv) > 1 else None

MATS = {
    "paint_a": (0.02, 0.02, 0.025, 0.75, 0.35, 0.0),
    "paint_b": (0.9, 0.91, 0.93, 0.65, 0.1, 0.0),
    "glass": (0.035, 0.045, 0.06, 0.92, 0.25, 0.0),
    "trim": (0.03, 0.03, 0.032, 0.35, 0.0, 0.0),
    "grille": (0.012, 0.012, 0.014, 0.25, 0.0, 0.0),
    "under": (0.015, 0.015, 0.016, 0.1, 0.0, 0.0),
    "tire": (0.022, 0.022, 0.022, 0.15, 0.0, 0.0),
    "rim": (0.55, 0.56, 0.58, 0.6, 0.9, 0.0),
    "chrome": (0.8, 0.8, 0.82, 0.9, 1.0, 0.0),
    "light_head": (1.0, 0.98, 0.9, 0.9, 0.0, 1.6),
    "light_tail": (0.85, 0.04, 0.04, 0.6, 0.0, 1.2),
    "decal": (1.0, 1.0, 1.0, 0.5, 0.0, 0.0),
}

DESIGNS = {
    # L length, W width, belt line, roof, c ground clearance, wb wheelbase, R wheel radius, hood / ws (windscreen run) /
    # rg (rear glass run) / trunk lengths; tumble = roof width / body width; nose / tail = rounded end lengths;
    # drop = how far the bonnet falls to the nose; sail = C-pillar length along the side
    "Interceptor": dict(L=5.10, W=1.96, belt=1.00, roof=1.47, c=0.17, wb=2.95, R=0.355, hood=1.38, ws=0.66, rg=0.56, trunk=1.02,
                        tumble=0.70, nose=0.42, tail=0.36, drop=0.12, sail=0.22),
    "Pursuit":     dict(L=4.95, W=1.98, belt=0.94, roof=1.33, c=0.14, wb=2.92, R=0.37, hood=1.62, ws=0.76, rg=1.0, trunk=0.62,
                        tumble=0.64, nose=0.55, tail=0.42, drop=0.16, sail=0.12),
    "Utility":     dict(L=5.05, W=2.02, belt=1.12, roof=1.84, c=0.24, wb=2.98, R=0.40, hood=1.12, ws=0.70, rg=0.22, trunk=0.18,
                        tumble=0.80, door0=-0.95, nose=0.40, tail=0.26, drop=0.09, sail=0.06, dpillar=True, crown=0.03, roofcrown=0.035),
}


LOFT_TOL = 0.005   # m: the largest deviation a dropped loft station may leave (about 0.25 px at 20 m on a 1080p screen)


def decimate(ys, sec, tag, n_seg, keep=()):
    """The stations to keep from the sorted list ys. Drops a station (the cheapest first) while the section there (sec(y):
    a list of (x, z)) is within LOFT_TOL of the interpolation between its kept neighbours and every one of the n_seg faces
    round the section has the same tag (tag(k, mid y)) on both sides of it and across the merged span. Ends and keep stay."""
    S = {y: sec(y) for y in ys}
    kept = list(ys)
    pinned = set(round(k, 3) for k in keep)

    def cost(i):
        a, y, b = kept[i - 1], kept[i], kept[i + 1]
        if round(y, 3) in pinned: return None
        t = (y - a) / (b - a)
        err = max(math.hypot(px - (ax + (bx - ax) * t), pz - (az + (bz - az) * t))
                  for (px, pz), (ax, az), (bx, bz) in zip(S[y], S[a], S[b]))
        if err > LOFT_TOL: return None
        for k in range(n_seg):
            if not (tag(k, (a + y) / 2) == tag(k, (y + b) / 2) == tag(k, (a + b) / 2)): return None
        return err

    while True:
        best, bi = None, -1
        for i in range(1, len(kept) - 1):
            c = cost(i)
            if c is not None and (best is None or c < best): best, bi = c, i
        if bi < 0: return kept
        del kept[bi]


def clear():
    for o in list(bpy.data.objects): bpy.data.objects.remove(o, do_unlink=True)
    for m in list(bpy.data.meshes): bpy.data.meshes.remove(m)


def mat(tag):
    m = bpy.data.materials.get("pc_" + tag) or bpy.data.materials.new("pc_" + tag)
    c = MATS[tag]; m.diffuse_color = (c[0], c[1], c[2], 1.0)
    return m


class Model:
    def __init__(self, name):
        self.name = name
        self.parts = []      # (part name, [(tag, bm-object)], pivot or None)
        self.objs = []

    def add(self, part, ob, tag, textured=False, pivot=None):
        self.objs.append((part, ob, tag, textured, pivot))


def obj_from(bm, name, tag, smooth=False):
    for f in bm.faces: f.smooth = smooth
    if smooth:   # smooth across gentle edges only: creases sharper than 50 degrees stay sharp
        for e in bm.edges:
            if len(e.link_faces) == 2 and e.calc_face_angle(0.0) > math.radians(50): e.smooth = False
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    me.materials.append(mat(tag))
    ob = bpy.data.objects.new(name, me); bpy.context.scene.collection.objects.link(ob)
    return ob


def box(M, part, name, c, size, tag, bevel=0.01, rot=None, segs=1):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=Vector(size), verts=bm.verts)
    if bevel > 0: bmesh.ops.bevel(bm, geom=list(bm.edges), offset=min(bevel, min(size) * 0.45), segments=segs, affect="EDGES")
    if rot is not None: bmesh.ops.transform(bm, matrix=rot.to_4x4(), verts=bm.verts)
    bmesh.ops.translate(bm, vec=Vector(c), verts=bm.verts)
    M.add(part, obj_from(bm, name, tag, smooth=segs > 1), tag)


def cyl(M, part, name, a, b, r, tag, segs=10, pivot=None, drop_cap=None):
    axis = Vector(b) - Vector(a); L = axis.length; axis.normalize()
    bm = bmesh.new(); bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segs, radius1=r, radius2=r, depth=L)
    q = Vector((0, 0, 1)).rotation_difference(axis); bmesh.ops.rotate(bm, verts=bm.verts, cent=Vector(), matrix=q.to_matrix())
    bmesh.ops.translate(bm, vec=(Vector(a) + Vector(b)) / 2, verts=bm.verts)
    if drop_cap is not None:   # the cap facing this way is hidden inside another part: left out
        bm.normal_update()
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.normal.dot(Vector(drop_cap)) > 0.99], context="FACES_ONLY")
    M.add(part, obj_from(bm, name, tag, smooth=True), tag, pivot=pivot)


def quad(M, part, name, pts, tag, uvs=None):
    bm = bmesh.new()
    vs = [bm.verts.new(p) for p in pts]
    f = bm.faces.new(vs)
    if uvs:
        uvl = bm.loops.layers.uv.new("UVMap")
        for loop, uv in zip(f.loops, uvs): loop[uvl].uv = uv
    M.add(part, obj_from(bm, name, tag), tag, textured=uvs is not None)


def build(name, P):
    clear()
    M = Model(name)
    L, W, belt, roof, c, wb, R = P["L"], P["W"], P["belt"], P["roof"], P["c"], P["wb"], P["R"]
    yF, yRr = L / 2, -L / 2
    yA = yF - P["hood"]                       # windscreen base
    yC = yRr + P["trunk"]                     # rear glass base
    yWs = yA - P["ws"]                        # windscreen top (raked back from its base)
    yRg = yC + P["rg"]                        # rear glass top
    wheels_y = (wb / 2, -wb / 2)
    Ra = R + 0.06                             # arch radius
    tumble = P.get("tumble", 0.70)            # roof width / body width (how much the glass leans in)
    RN, RT = P.get("nose", 0.42), P.get("tail", 0.36)   # length of the rounded nose / tail

    def ends(y):
        """0..1 into the rounded nose (sf) and tail (sr)."""
        sf = min(1.0, max(0.0, (y - (yF - RN)) / RN)); sr = min(1.0, max(0.0, ((yRr + RT) - y) / RT))
        return sf, sr

    def hw(y):
        sf, sr = ends(y)
        f = min(1.0, max(0.0, (y - (yF - 1.0)) / 1.0)); r_ = min(1.0, max(0.0, ((yRr + 0.9) - y) / 0.9))
        base = W / 2 * (1 - 0.05 * f * f - 0.04 * r_ * r_)            # gentle taper in plan towards both ends
        return base * math.sqrt(max(0.0, 1 - (0.55 * sf) ** 2)) * math.sqrt(max(0.0, 1 - (0.5 * sr) ** 2))   # round corners

    def zt(y):
        sf, sr = ends(y)
        if y > yA:   # bonnet curves down to the nose
            t = (y - yA) / (yF - yA)
            z = belt - P.get("drop", 0.12) * t ** 1.6
        elif y < yC:   # boot lid, rounding over at the tail
            t = (yC - y) / max(0.01, yC - yRr)
            z = belt - 0.03 * t ** 2
        else: z = belt
        return z - 0.14 * sf ** 2.2 - 0.12 * sr ** 2.2

    def zb(y):
        sf, sr = ends(y)
        z = c + 0.16 * sf ** 1.8 + 0.12 * sr ** 1.8   # bumpers tuck up at both ends
        for yw in wheels_y:
            d = y - yw
            if abs(d) < Ra: z = max(z, R + math.sqrt(Ra * Ra - d * d))
        return z

    def section(y):
        """14-point closed section, right side bottom to top, then left side top to bottom."""
        h, top, bot = hw(y), zt(y), zb(y)
        Hs = max(0.06, top - bot)
        crown = P.get("crown", 0.035)
        right = [(h * 0.84, bot), (h * 0.96, bot + 0.12 * Hs), (h, bot + 0.42 * Hs), (h * 0.99, bot + 0.60 * Hs),
                 (h * 0.95, top - min(0.07, 0.25 * Hs)), (h * 0.80, top - 0.012), (h * 0.40, top + crown * 0.8)]
        return right + [(-x, z) for (x, z) in reversed(right)]

    def side_x(y, z):
        """Outer body surface x at height z (right side), from the section."""
        s = section(y)[:7]
        for (x0, z0), (x1, z1) in zip(s, s[1:]):
            if z0 <= z <= z1 and z1 > z0: return x0 + (x1 - x0) * (z - z0) / (z1 - z0)
        return s[2][0]

    # stations: every 0.25 m, dense around the arches and the rounded ends
    ys = set(round(yRr + i * 0.25, 3) for i in range(int(L / 0.25) + 1)) | {round(yF, 3), round(yA, 3), round(yC, 3)}
    for yw in wheels_y:
        for k in range(-6, 7): ys.add(round(yw + Ra * k / 6.0, 3))
    for k in range(1, 6):
        ys.add(round(yF - RN * (k / 6.0) ** 0.7, 3)); ys.add(round(yRr + RT * (k / 6.0) ** 0.7, 3))
    ys = sorted(y for y in ys if yRr <= y <= yF)

    # ---- lower body loft, faces tagged by region
    door0, door1 = P.get("door0", yC + 0.15), yA - 0.25         # the doors run from just ahead of the rear glass to just behind the windscreen
    N = 14

    def lower_tag(k, ym):
        if k == 13: return "under"
        if k in (3, 9) and ym > yF - 0.13: return "light_head"
        if k in (3, 9) and ym < yRr + 0.11: return "light_tail"
        if k in (1, 2, 3, 4, 8, 9, 10, 11) and door0 < ym < door1: return "paint_b"
        return "paint_a"

    lys = decimate(ys, section, lower_tag, N, keep=(yA, yC))
    bm = bmesh.new(); faces = []; rings = []
    for y in lys:
        rings.append([bm.verts.new((x, y, z)) for (x, z) in section(y)])
    for i in range(len(rings) - 1):
        A, B = rings[i], rings[i + 1]
        ym = (lys[i] + lys[i + 1]) / 2
        for k in range(N):
            f = bm.faces.new((A[k], A[(k + 1) % N], B[(k + 1) % N], B[k]))
            faces.append((f, lower_tag(k, ym)))
    faces.append((bm.faces.new(list(reversed(rings[0]))), "paint_a"))
    faces.append((bm.faces.new(rings[-1]), "paint_a"))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    split_add(M, bm, faces, "Body")

    # ---- greenhouse loft: curved side glass, crowned roof, glass that rounds into the roof
    def roofz(y):
        if y > yWs: t = (yA - y) / (yA - yWs); return belt + (roof - belt) * (1 - (1 - t) ** 1.7)
        if y < yRg: t = (y - yC) / max(0.01, yRg - yC); return belt + (roof - belt) * (1 - (1 - t) ** 1.7)
        return roof
    yB = (yWs + yRg) / 2 + 0.1
    gset = set(round(y, 3) for y in ys if yC <= y <= yA) | {round(yA, 3), round(yC, 3), round(yWs, 3), round(yRg, 3)}
    for k in range(1, 6):
        gset.add(round(yA - (yA - yWs) * k / 6, 3)); gset.add(round(yC + (yRg - yC) * k / 6, 3))
    for yy in (yB - 0.05, yB + 0.05): gset.add(round(yy, 3))
    yD = None
    if P.get("dpillar"):
        yD = (yRg + yB) / 2 - 0.15
        for yy in (yD - 0.045, yD + 0.045): gset.add(round(yy, 3))
    gys = sorted(gset)

    def gsection(y):
        h = hw(y); hb = h * 0.80; z0 = zt(y) - 0.02; z1 = roofz(y)
        rise = max(0.0, z1 - z0)
        ht = h * (tumble + (0.80 - tumble) * (1 - min(1.0, rise / max(0.01, roof - belt))))   # narrower where the glass is tall
        hm = hb + (ht - hb) * 0.45 + 0.015          # side glass bulges out a little
        crown = P.get("roofcrown", 0.04) * min(1.0, rise / max(0.01, roof - belt))
        R_ = [(hb, z0), (hm, z0 + rise * 0.5), (ht, z1 - 0.03 * min(1.0, rise / 0.2)), (ht * 0.86, z1)]
        return R_ + [(0.0, z1 + crown)] + [(-x, z) for (x, z) in reversed(R_)]

    def glass_tag(k, ym):
        roofzone = yRg <= ym <= yWs
        if k in (3, 4): return "paint_b" if roofzone else "glass"             # roof / windscreen and rear glass
        if k in (2, 5): return "paint_b" if roofzone else "paint_a"           # roof edge / A and C pillars
        # side glass, B (and D) pillars, C-pillar sail
        if abs(ym - yB) < 0.05 or (yD is not None and abs(ym - yD) < 0.045): return "trim"
        if ym < yRg + P.get("sail", 0.18) or ym > yWs + 0.02: return "paint_a" if ym < yRg + P.get("sail", 0.18) else "glass"
        return "glass"

    gys = decimate(gys, gsection, glass_tag, 8, keep=(yA, yC, yWs, yRg))
    bm = bmesh.new(); grings = []
    for y in gys:
        grings.append([bm.verts.new((x, y, z)) for (x, z) in gsection(y)])
    gfaces = []
    for i in range(len(grings) - 1):
        A, B = grings[i], grings[i + 1]
        ym = (gys[i] + gys[i + 1]) / 2
        for k in range(8):
            f = bm.faces.new((A[k], A[k + 1], B[k + 1], B[k]))
            gfaces.append((f, glass_tag(k, ym)))
    for f in bm.faces:   # outward: away from the car's centre line, upward on top
        cen = f.calc_center_median()
        if f.normal.dot(Vector((cen.x, 0, cen.z - (belt + roof) / 2))) < 0: f.normal_flip()
    split_add(M, bm, gfaces, "Body")

    # ---- wheel wells (hide the see-through arches), arch flares and wheels
    for yw in wheels_y:
        for s in (-1, 1):
            bm = bmesh.new(); segs = 10; arcs = []
            for x in (s * (hw(yw) - 0.02), s * (hw(yw) - 0.42)):
                arcs.append([bm.verts.new((x, yw + math.cos(math.pi * k / segs) * Ra, R + math.sin(math.pi * k / segs) * Ra)) for k in range(segs + 1)])
            for k in range(segs):
                bm.faces.new((arcs[0][k], arcs[0][k + 1], arcs[1][k + 1], arcs[1][k]))
            for f in bm.faces:   # facing down / into the arch
                if f.normal.dot(f.calc_center_median() - Vector((f.calc_center_median().x, yw, R))) > 0: f.normal_flip()
            M.add("Body", obj_from(bm, f"Well{yw:+.1f}{s}", "under"), "under")
            # flare: a rounded lip standing proud of the body around the arch
            bm = bmesh.new(); rows = []
            for (dr, dx) in ((-0.005, 0.0), (0.035, 0.022), (0.07, 0.0)):
                row = []
                for k in range(segs + 1):
                    a = math.pi * k / segs
                    yy, zz = yw + math.cos(a) * (Ra + dr), R + math.sin(a) * (Ra + dr)
                    if zz < c + 0.02: zz = c + 0.02
                    row.append(bm.verts.new((s * (side_x(yy, min(zz, zt(yy) - 0.05)) + dx), yy, zz)))
                rows.append(row)
            fl = []
            for r in range(2):
                for k in range(segs): fl.append(bm.faces.new((rows[r][k], rows[r][k + 1], rows[r + 1][k + 1], rows[r + 1][k])))
            for f in fl:
                if f.normal.x * s < 0: f.normal_flip()
            M.add("Body", obj_from(bm, f"Flare{yw:+.1f}{s}", "paint_a", smooth=True), "paint_a")
            # the wheel: tyre, rim face and a hub, around its own pivot (spins about local x)
            px = s * (hw(yw) - 0.16)
            piv = Vector((px, yw, R))
            part = f"Wheel{'F' if yw > 0 else 'R'}{'L' if s < 0 else 'R'}"
            cyl(M, part, part + "_tire", (-0.12, 0, 0), (0.12, 0, 0), R, "tire", segs=16, pivot=piv)
            # rim and hub: their inner caps sit inside the tyre / the rim and are never seen
            cyl(M, part, part + "_rim", (s * 0.105, 0, 0), (s * 0.125, 0, 0), R * 0.64, "rim", segs=16, pivot=piv, drop_cap=(-s, 0, 0))
            cyl(M, part, part + "_hub", (s * 0.12, 0, 0), (s * 0.14, 0, 0), R * 0.18, "chrome", segs=8, pivot=piv, drop_cap=(-s, 0, 0))

    # ---- front: the lights are part of the body surface round the nose corners (loft faces tagged light_head) plus a
    # lens on the nose face in the same band; grille between them
    def band(y):
        sec = section(y); return sec[3][1], sec[4][1]
    za, zb2 = band(yF)
    for s in (-1, 1):
        xa, xb = side_x(yF, za) - 0.012, side_x(yF, zb2) - 0.012
        pts = [(s * 0.50, yF + 0.004, za), (s * xa, yF + 0.004, za), (s * xb, yF + 0.004, zb2), (s * 0.50, yF + 0.004, zb2)]
        quad(M, "Body", f"Head{s}", pts if s > 0 else [pts[1], pts[0], pts[3], pts[2]], "light_head")
    quad(M, "Body", "Grille", [(-0.42, yF + 0.004, za - 0.10), (0.42, yF + 0.004, za - 0.10), (0.40, yF + 0.004, zb2), (-0.40, yF + 0.004, zb2)], "grille")
    zf = zb2
    bumper(M, "BumperF", yF + 0.06, c + 0.21, 2 * hw(yF - 0.12) * 0.94, 1)
    for s in (-1, 1):
        cyl(M, "Body", f"PushV{s}", (s * 0.34, yF + 0.13, c + 0.1), (s * 0.34, yF + 0.13, zf + 0.0), 0.028, "trim", segs=6)
    cyl(M, "Body", "PushH1", (-0.4, yF + 0.14, c + 0.34), (0.4, yF + 0.14, c + 0.34), 0.024, "trim", segs=6)
    cyl(M, "Body", "PushH2", (-0.4, yF + 0.14, zf - 0.06), (0.4, yF + 0.14, zf - 0.06), 0.024, "trim", segs=6)

    # ---- rear: tail lights wrap round the tail corners the same way
    za, zb2 = band(yRr)
    for s in (-1, 1):
        xa, xb = side_x(yRr, za) - 0.012, side_x(yRr, zb2) - 0.012
        pts = [(s * xa, yRr - 0.004, za), (s * 0.42, yRr - 0.004, za), (s * 0.42, yRr - 0.004, zb2), (s * xb, yRr - 0.004, zb2)]
        quad(M, "Body", f"Tail{s}", pts if s > 0 else [pts[1], pts[0], pts[3], pts[2]], "light_tail")
    bumper(M, "BumperR", yRr - 0.06, c + 0.22, 2 * hw(yRr + 0.12) * 0.94, -1)

    # ---- mirrors (rounded pods on a stalk), spotlight
    for s in (-1, 1):
        mx = s * (side_x(yA - 0.1, belt - 0.05) + 0.1)
        box(M, "Body", f"Mirror{s}", (mx, yA - 0.14, belt + 0.07), (0.17, 0.08, 0.11), "paint_a", bevel=0.035, segs=1)
        box(M, "Body", f"MirrorStalk{s}", (mx - s * 0.07, yA - 0.12, belt + 0.02), (0.08, 0.04, 0.03), "trim", bevel=0.01)
    cyl(M, "Body", "Spot", (-(hw(yA) * 0.82), yA - 0.02, belt + 0.1), (-(hw(yA) * 0.82), yA + 0.12, belt + 0.1), 0.05, "chrome", segs=8)

    # ---- door decals, reading forwards on each side, following the curved door (3 rows)
    dy0 = max(door0 + 0.12, wheels_y[1] + Ra + 0.08); dy1 = min(door1 - 0.1, wheels_y[0] - Ra - 0.08)
    dl = min(dy1 - dy0, 1.6); dc = (dy0 + dy1) / 2; dh = dl / 4
    dz0 = belt - 0.12 - dh
    for s in (-1, 1):
        for r in range(3):
            za, zb_ = dz0 + dh * r / 3, dz0 + dh * (r + 1) / 3
            xa, xb = s * (side_x(dc, za) + 0.006), s * (side_x(dc, zb_) + 0.006)
            va, vb = r / 3, (r + 1) / 3
            p = [(xa, dc - dl / 2, za), (xa, dc + dl / 2, za), (xb, dc + dl / 2, zb_), (xb, dc - dl / 2, zb_)]
            uv = [(0, va), (1, va), (1, vb), (0, vb)] if s > 0 else [(1, va), (0, va), (0, vb), (1, vb)]
            pts = p if s > 0 else [p[1], p[0], p[3], p[2]]
            uvs = uv if s > 0 else [uv[1], uv[0], uv[3], uv[2]]
            quad(M, "Body", f"Decal{s}_{r}", pts, "decal", uvs=uvs)

    roof_c = ((yWs + yRg) / 2 + 0.05, roof + P.get("roofcrown", 0.04))
    return M, roof_c


def split_add(M, bm, faces, part):
    """One Blender object per material tag from a tagged bmesh (frees bm)."""
    bm.faces.index_update()
    idx = {f: f.index for f, _ in faces}
    by = {}
    for f, t in faces: by.setdefault(t, set()).add(idx[f])
    for t, keep in by.items():
        b2 = bm.copy()
        b2.faces.ensure_lookup_table()
        bmesh.ops.delete(b2, geom=[f for f in b2.faces if f.index not in keep], context="FACES")
        M.add(part, obj_from(b2, f"{part}_{t}_{len(M.objs)}", t, smooth=True), t)
    bm.free()


def bumper(M, name, y, z, width, sgn):
    """A rounded bumper bar: a capsule-section loft across the car that wraps back at the corners."""
    bm = bmesh.new(); segs = 8; rings = []
    hgt, dep = 0.2, 0.18
    us = (-1.0, -0.82, -0.58, -0.25, 0.25, 0.58, 0.82, 1.0)   # dense where the corners sweep back (|u|^3), sparse across the straight middle
    cols = len(us) - 1
    for u in us:
        x = u * width / 2
        back = 0.16 * abs(u) ** 3                   # corners sweep back along the sides
        ring = []
        for k in range(segs):
            a = 2 * math.pi * k / segs
            ring.append(bm.verts.new((x, y - sgn * (back + dep / 2) + sgn * math.cos(a) * dep / 2, z + math.sin(a) * hgt / 2)))
        rings.append(ring)
    for i in range(cols):
        for k in range(segs):
            bm.faces.new((rings[i][k], rings[i][(k + 1) % segs], rings[i + 1][(k + 1) % segs], rings[i + 1][k]))
    bm.faces.new(rings[0]); bm.faces.new(list(reversed(rings[-1])))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    M.add("Body", obj_from(bm, name, "trim", smooth=True), "trim")


def fmt(v): return " ".join(f"{c:.5f}" for c in v)


def export(M, roof_c, path):
    lines = [f"# Police plugin car model '{M.name}' (built by police_models.py; Unity axes, metres, ground at y = 0)",
             f"name {M.name}", f"roof 0.00000 {roof_c[1]:.5f} {roof_c[0]:.5f}", "tex decal police_decal.png"]
    for t, c in MATS.items(): lines.append(f"mat {t} " + " ".join(f"{x:.4f}" for x in c))
    parts = {}
    for (part, ob, tag, textured, pivot) in M.objs: parts.setdefault(part, []).append((ob, tag, textured, pivot))
    ntri = nvert = 0
    for part, items in parts.items():
        lines.append(f"o {part}")
        piv = items[0][3]
        if piv is not None: lines.append(f"p {piv.x:.5f} {piv.z:.5f} {piv.y:.5f}")
        # welded within each tag: corners with the same position, normal and uv share one vertex (none spans two tags)
        verts, index, tris = [], {}, {}
        for ob, tag, textured, pivot in items:
            me = ob.data; me.calc_loop_triangles()
            uvl = me.uv_layers.active.data if (textured and me.uv_layers) else None
            for lt in me.loop_triangles:
                tri = []
                for li in (lt.loops[0], lt.loops[2], lt.loops[1]):
                    v = me.vertices[me.loops[li].vertex_index].co
                    n = me.corner_normals[li].vector.normalized()
                    s = fmt((v.x, v.z, v.y)) + " " + fmt((n.x, n.z, n.y))
                    if uvl is not None: s += f" {uvl[li].uv.x:.5f} {uvl[li].uv.y:.5f}"
                    key = (tag, s)
                    if key not in index: index[key] = len(verts); verts.append(s)
                    tri.append(index[key])
                tris.setdefault(tag, []).append(tri)
        lines.extend("v " + s for s in verts)
        for tag, ts in tris.items():
            lines.append(f"m {tag}")
            lines.extend(f"t {a} {b} {c}" for a, b, c in ts)
            ntri += len(ts)
        nvert += len(verts)
    open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    return ntri, nvert


def pbr(outdir):
    """Principled materials matching the Unity ones (colour, smoothness -> roughness, metallic, emission; decal alpha-clipped)."""
    for tag, c in MATS.items():
        m = bpy.data.materials.get("pc_" + tag)
        if m is None: continue
        m.use_nodes = True
        nt = m.node_tree; b = nt.nodes.get("Principled BSDF")
        b.inputs["Base Color"].default_value = (c[0], c[1], c[2], 1)
        b.inputs["Roughness"].default_value = 1 - c[3]
        b.inputs["Metallic"].default_value = c[4]
        if c[5] > 0:
            b.inputs["Emission Color"].default_value = (c[0], c[1], c[2], 1); b.inputs["Emission Strength"].default_value = c[5]
        if tag == "decal":
            img = bpy.data.images.load(os.path.join(OUT, "police_decal.png"))
            t = nt.nodes.new("ShaderNodeTexImage"); t.image = img
            nt.links.new(t.outputs["Color"], b.inputs["Base Color"])
            # alpha clip at 0.5 like the Unity material
            g = nt.nodes.new("ShaderNodeMath"); g.operation = "GREATER_THAN"; g.inputs[1].default_value = 0.5
            nt.links.new(t.outputs["Alpha"], g.inputs[0]); nt.links.new(g.outputs[0], b.inputs["Alpha"])
            try: m.surface_render_method = "DITHERED"
            except Exception: pass


def preview(M, name, outdir):
    # wheel parts were built at the origin of their pivot: move them for the picture
    for (part, ob, tag, textured, pivot) in M.objs:
        if pivot is not None: ob.location = pivot
    pbr(outdir)
    sc = bpy.context.scene
    cd = bpy.data.cameras.new("C"); cam = bpy.data.objects.new("C", cd); sc.collection.objects.link(cam); sc.camera = cam
    cd.lens = 45
    sc.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE_NEXT"
    sc.eevee.taa_render_samples = 24
    sc.render.resolution_x, sc.render.resolution_y = 960, 540
    sc.view_settings.view_transform = "Standard"
    # sky-ish world + a sun, and a ground plane for contact
    w = bpy.data.worlds.new("W"); sc.world = w; w.use_nodes = True
    bg = w.node_tree.nodes.get("Background"); bg.inputs[0].default_value = (0.55, 0.6, 0.68, 1); bg.inputs[1].default_value = 0.9
    sd = bpy.data.lights.new("Sun", "SUN"); sd.energy = 3.5; sun = bpy.data.objects.new("Sun", sd); sc.collection.objects.link(sun)
    sun.rotation_euler = (math.radians(50), 0, math.radians(35))
    bpy.ops.mesh.primitive_plane_add(size=40, location=(0, 0, 0))
    gm = bpy.data.materials.new("ground"); gm.use_nodes = True
    gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.09, 0.09, 0.1, 1)
    gm.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.85
    bpy.context.active_object.data.materials.append(gm)
    for view, loc in (("front", (5.2, 6.2, 2.0)), ("rear", (-4.8, -6.4, 2.2)), ("side", (8.5, 0.0, 1.2))):
        cam.location = loc
        d = Vector((0, 0, 0.7)) - Vector(loc)
        cam.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
        sc.render.filepath = os.path.join(outdir, f"{name}_{view}.png")
        bpy.ops.render.render(write_still=True)


os.makedirs(OUT, exist_ok=True)
for name, P in DESIGNS.items():
    M, roof_c = build(name, P)
    n, nv = export(M, roof_c, os.path.join(OUT, f"{name}.pcm"))
    print(f"[{name}] {n} triangles, {nv} vertices")
    if PREVIEW: preview(M, name, PREVIEW)
