"""Removes cockpit triangles that no camera can ever see, without changing the look. Run after build_interior.py.

blender -b --factory-startup --python optimize_dcm.py -- <in.dcm> <out.dcm> [--dry]

Only "enclosed" triangles go: both sides of the triangle are sealed off by geometry of the SAME group (groups are
moved as rigid units by the per-car Part.<Group> layout, so a triangle sealed by its own group stays sealed whatever
the layout, seat position or head turn). A side counts as sealed when every probe ray from it (28 points spread over
the triangle, corners and edges included, x 32 directions over that side's hemisphere, starting 1 mm off the surface) either
  - hits same-group geometry within SEAL_DIST (a gap smaller than any camera: the eye's near clip is 3 cm and the
    mirror cameras sit 3 cm in front of their glass), or
  - first hits the BACK of a same-group face within INSIDE_DIST (the point is inside a closed solid).
A ray that escapes (no hit, or a far front-facing hit) means some camera could look at that side: the triangle stays.
Both sides are tested because the cartoon outline draws back faces (inverted hull).

Also dropped: zero-area (degenerate) triangles of any static part (they draw nothing, whatever their tag), and `mat`
lines whose tag no remaining triangle uses (DriverCam only reads them to colour triangles of that tag).

Never touched: pivot parts (steering wheel, needles, digital gauges: they move, and the gauges address their quads
by order), mirror glass, textured tags (alpha cutout), `paint`. Every other line of the file is kept byte for byte,
so the header, `tex` lines, gauge lines and part order are unchanged. Prints a per-group summary.
"""
import sys, math
from mathutils import Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index("--") + 1:]
SRC, DST = argv[0], argv[1]
DRY = "--dry" in argv

SEAL_DIST = 0.08
INSIDE_DIST = 0.6
OFFSET = 0.001
GRID = 6                 # probe points per triangle: (GRID+1)(GRID+2)/2 = 28
KEEP_TAGS = {"paint", "mirror_glass", "mirror_left", "mirror_right"}

lines = open(SRC, encoding="utf-8").read().split("\n")
textured = set()
tris = []        # dict(line, group, part, pivot, tag, v=[Vector]*3, n=Vector)
part = None; group = "Misc"; pivot = False; tag = "interior_dark"
for i, raw in enumerate(lines):
    t = raw.split()
    if not t or t[0].startswith("#"): continue
    k = t[0]
    if k == "tex": textured.add(t[1])
    elif k == "o": part = t[1]; group = "Misc"; pivot = False; tag = "interior_dark"
    elif k == "g": group = t[1]
    elif k == "p": pivot = True
    elif k == "m": tag = t[1]
    elif k in ("f", "u"):
        st = 8 if k == "u" else 6
        vs, ns = [], []
        for c in range(3):
            b = 1 + c * st
            vs.append(Vector((float(t[b]), float(t[b + 1]), float(t[b + 2]))))
            ns.append(Vector((float(t[b + 3]), float(t[b + 4]), float(t[b + 5]))))
        n = ns[0] + ns[1] + ns[2]
        if n.length < 1e-6: n = (vs[1] - vs[0]).cross(vs[2] - vs[0])
        tris.append({"line": i, "group": group, "part": part, "pivot": pivot, "tag": tag, "v": vs,
                     "n": n.normalized() if n.length > 1e-9 else Vector((0, 0, 0))})

# probe directions: a Fibonacci sphere
DIRS = []
N = 64
for j in range(N):
    y = 1 - 2 * (j + 0.5) / N
    r = math.sqrt(max(0.0, 1 - y * y)); phi = j * math.pi * (3 - math.sqrt(5))
    DIRS.append(Vector((math.cos(phi) * r, y, math.sin(phi) * r)))

# one BVH per group over its static (non-pivot) triangles
groups = {}
for idx, tr in enumerate(tris):
    if tr["pivot"]: continue
    groups.setdefault(tr["group"], []).append(idx)

removed = set()
summary = []
for gname, idxs in groups.items():
    verts, polys, owner = [], [], []
    for idx in idxs:
        tr = tris[idx]
        b = len(verts); verts.extend(tr["v"]); polys.append((b, b + 1, b + 2)); owner.append(idx)
    bvh = BVHTree.FromPolygons(verts, polys, all_triangles=True, epsilon=0.0)

    def side_sealed(points, n):
        for p in points:
            o = p + n * OFFSET
            for d in DIRS:
                if d.dot(n) <= 0.05: continue
                loc, _hn, fi, dist = bvh.ray_cast(o, d, 10.0)
                if loc is None: return False
                if dist <= SEAL_DIST: continue
                hit_n = tris[owner[fi]]["n"]
                if dist <= INSIDE_DIST and hit_n.dot(d) > 0.0: continue   # hit a face from behind: inside a solid
                return False
        return True

    cut = 0
    for idx in idxs:
        tr = tris[idx]
        a, b, c = tr["v"]
        area2 = (b - a).cross(c - a).length
        if area2 < 1e-9:              # degenerate (zero area): draws nothing, whatever its tag
            removed.add(tr["line"]); cut += 1; continue
        if tr["tag"] in KEEP_TAGS or tr["tag"] in textured: continue
        n = tr["n"]
        if n.length < 0.5: continue
        # a barycentric grid over the whole triangle, corners and edges included (pulled 1% inwards): a triangle that
        # is only partly covered (e.g. a bezel rim around its glass) keeps a probe point on its uncovered part
        pts = []
        for i in range(GRID + 1):
            for j in range(GRID + 1 - i):
                u, v = i / GRID, j / GRID
                w = 1.0 - u - v
                u, v, w = (0.98 * x + 0.02 / 3 for x in (u, v, w))
                pts.append(a * u + b * v + c * w)
        if side_sealed(pts, n) and side_sealed(pts, -n):
            removed.add(tr["line"]); cut += 1
    summary.append((gname, len(idxs), cut))

total = len(tris)
print(f"OPTIMIZE {SRC}")
for g, n, c in summary: print(f"  group {g:14s} static tris {n:5d}  removed {c:5d}")
ntri_cut = sum(1 for tr in tris if tr["line"] in removed)
print(f"  total tris {total} -> {total - ntri_cut} ({ntri_cut} removed, {100.0 * ntri_cut / max(1, total):.1f}%)")
# `mat` lines of tags that no remaining triangle uses
used = {tr["tag"] for tr in tris if tr["line"] not in removed}
for i, raw in enumerate(lines):
    t = raw.split()
    if len(t) > 1 and t[0] == "mat" and t[1] not in used:
        removed.add(i); print(f"  unused mat {t[1]} dropped")
if not DRY:
    out = [l for i, l in enumerate(lines) if i not in removed]
    open(DST, "w", encoding="utf-8", newline="\n").write("\n".join(out))
    print(f"  wrote {DST}")
