"""Mesh builders for the cabin trim in build_interior.py (Blender's Python, bmesh). Pure geometry: each returns a bmesh
that build_interior.finish() turns into a tagged object. Every solid is closed and its normals point outwards (the game
culls back faces and draws its cartoon outline from the back faces, so an open edge would show as a hole or a seam).

Conventions (Blender, body frame): x right, y forward, z up, metres.
"""
import math
import bmesh
from mathutils import Vector


def rounded_profile(w, h, r_top, r_bot=0.0, segs=3):
    """A closed 2D outline (u across, v up), counter-clockwise, w wide and h tall with its base centred on (0, 0):
    the top corners rounded with radius r_top and the bottom ones with r_bot (0 = square)."""
    r_top = min(r_top, w / 2, h / 2); r_bot = min(r_bot, w / 2, h / 2)
    pts = []

    def corner(cx, cy, r, a0):
        if r <= 1e-6:
            pts.append((cx, cy)); return
        for k in range(segs + 1):
            a = math.radians(a0 + 90.0 * k / segs)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    corner(w / 2 - r_bot, r_bot, r_bot, -90)           # bottom right
    corner(w / 2 - r_top, h - r_top, r_top, 0)         # top right
    corner(-w / 2 + r_top, h - r_top, r_top, 90)       # top left
    corner(-w / 2 + r_bot, r_bot, r_bot, 180)          # bottom left
    out = []
    for p in pts:   # drop repeated points (square corners next to round ones)
        if not out or (abs(out[-1][0] - p[0]) > 1e-7 or abs(out[-1][1] - p[1]) > 1e-7): out.append(p)
    if len(out) > 1 and abs(out[0][0] - out[-1][0]) < 1e-7 and abs(out[0][1] - out[-1][1]) < 1e-7: out.pop()
    return out


def round_profile(r, segs=8):
    return [(r * math.cos(2 * math.pi * k / segs), r * math.sin(2 * math.pi * k / segs)) for k in range(segs)]


def loft(rings, closed_ends=True, bm=None):
    """Side quads between consecutive rings (lists of Vectors, same length, each a closed loop) and, with closed_ends,
    an n-gon on each end. Normals recalculated outwards."""
    bm = bm or bmesh.new()
    vr = [[bm.verts.new(p) for p in ring] for ring in rings]
    m = len(rings[0])
    new = []
    for i in range(len(vr) - 1):
        A, B = vr[i], vr[i + 1]
        for k in range(m):
            try: new.append(bm.faces.new((A[k], A[(k + 1) % m], B[(k + 1) % m], B[k])))
            except ValueError: pass
    if closed_ends:
        for ring in (vr[0], vr[-1]):
            try: new.append(bm.faces.new(ring))
            except ValueError: pass
    bmesh.ops.recalc_face_normals(bm, faces=new)
    return bm


def sweep(path, profile, side_hint, scales=None, bm=None, closed_ends=True):
    """Sweeps a 2D profile (u, v) along a polyline. At each path point the profile's u axis is `side_hint(i)` made
    perpendicular to the path, v = tangent x u... flipped so v points along side_hint's 'up' companion: v = u x t.
    scales: optional per-point (su, sv) factors (tapering)."""
    rings = []
    n = len(path)
    for i in range(n):
        t = (path[min(i + 1, n - 1)] - path[max(i - 1, 0)]).normalized()
        u = side_hint(i); u = (u - t * u.dot(t)).normalized()
        v = t.cross(u).normalized()
        su, sv = scales[i] if scales else (1.0, 1.0)
        rings.append([path[i] + u * (a * su) + v * (b * sv) for (a, b) in profile])
    return loft(rings, closed_ends, bm)


def sheet_solid(grid, thickness, away, bm=None):
    """A thin closed panel from a grid of points (rows x cols): the grid is the visible face, a copy offset by
    `thickness` along the per-point direction away(p) (unit Vector, pointing away from the viewer) is the back, and
    the border is closed. Normals outwards."""
    bm = bm or bmesh.new()
    R, C = len(grid), len(grid[0])
    front = [[bm.verts.new(p) for p in row] for row in grid]
    back = [[bm.verts.new(p + away(p) * thickness) for p in row] for row in grid]
    faces = []
    for i in range(R - 1):
        for j in range(C - 1):
            faces.append(bm.faces.new((front[i][j], front[i][j + 1], front[i + 1][j + 1], front[i + 1][j])))
            faces.append(bm.faces.new((back[i][j], back[i + 1][j], back[i + 1][j + 1], back[i][j + 1])))
    border = [(0, j) for j in range(C - 1)] + [(i, C - 1) for i in range(R - 1)] + \
             [(R - 1, j) for j in range(C - 1, 0, -1)] + [(i, 0) for i in range(R - 1, 0, -1)]
    for k in range(len(border)):
        (i0, j0), (i1, j1) = border[k], border[(k + 1) % len(border)]
        try: faces.append(bm.faces.new((front[i0][j0], front[i1][j1], back[i1][j1], back[i0][j0])))
        except ValueError: pass
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return bm


def polygon_slab(outline, normal, thickness, bevel=0.0, bm=None):
    """A flat closed slab: a convex-ish polygon outline (Vectors, in order) extruded by `thickness` along -normal
    (normal = the visible side). Optional bevel on its edges (rounded look)."""
    bm = bm or bmesh.new()
    n = normal.normalized()
    top = [bm.verts.new(p) for p in outline]
    bot = [bm.verts.new(p - n * thickness) for p in outline]
    m = len(outline)
    faces = [bm.faces.new(top), bm.faces.new(list(reversed(bot)))]
    for k in range(m):
        faces.append(bm.faces.new((top[k], bot[k], bot[(k + 1) % m], top[(k + 1) % m])))
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    if bevel > 0:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=min(bevel, thickness * 0.45), segments=1, affect="EDGES", profile=0.5)
    return bm


def triangulated_count(bm):
    return sum(len(f.verts) - 2 for f in bm.faces)
