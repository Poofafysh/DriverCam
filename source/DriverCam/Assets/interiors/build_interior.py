"""Builds a realistic interior for one car and writes a new cockpit .dcm.

blender -b --factory-startup --python build_interior.py -- <Car> <cockpit_Car.dcm> <out dir> [preview dir] [car .cfg | live]

Input cockpit: the car's layout-1 auto-fit (the cockpit autofit.py fitted to the car's body before any modelled
interior). It is not kept in the repo as a file: it is the shipped cockpit at BASE_REV (the last layout-1 cockpits),
read with `git show BASE_REV:source/DriverCam/Assets/cockpits/cockpit_<Car>.dcm` into a scratch file (the system temp
folder, or $DCM_BASE_DIR). A layout-2 file (a cockpit this script already built, e.g. the repo's own) is swapped for
that base automatically, so a rebuild never stacks a new cabin on an old one; pass `base` as the cockpit to ask for it.
Its Shell, Doors and side-mirror groups are the autofit of the game's car body; the rebuilt file says so in its header.

Car setup: the 5th argument. `live` = the setup the game really uses: `<GameDir>/BepInEx/config/DriverCam_cars/<Car>.cfg`
(falling back to the shared copy in `plugins/DriverCam/cars/`), GameDir from source/local.props. Only read, never
written. A setup whose Part.Interior is identity (already migrated to layout 2) gets the layout-1 offset from
specs.OLD_INTERIOR, so the cabin is built where the tuned old dash sat.

Keeps every part of the old cockpit that was fitted to the car's body (shell, doors, side mirrors, the rear-view mirror
glass) and replaces the 'Interior' and 'SteeringWheel' groups with a modelled cabin from specs.SPECS[car]: dash shaped
after the real car, instrument binnacle with textured gauges, centre stack and console, shifter, seats, pedals, carpet
and the real car's style of steering wheel. The bare boxes of the auto-fit (A-pillar bars, roof slab, header, visors)
are replaced by a 'Trim' group modelled where the tuned ones showed: headliner (with side valances), pillar trims, window frames, door-top
caps, B / C pillars, rear window surround, visors, grab handles and dome light (see specs.SPECS[car]["trim"]). The
rear-view mirror keeps its glass and gets a rounded housing in its own group. No cabin piece may hide a mirror from
the tuned eye: where one reaches into the eye-to-mirror frustum, that frustum is cut out of it (see "mirror
sightlines"; behind the rear-view mirror's cut in the headliner, a closed recess). Everything is anchored to the old
cockpit's eye point, steering-wheel pivot, windscreen base (A-pillar foot) and floor, so it lines up with the body
and keeps the per-car Edit-mode offsets meaningful. Adds `mat` lines (the model's own colours) and `tex gauges` /
`tex gauges_mph` lines.

Working gauges: the speedometer and tachometer get needles as their own pivot parts (`p` line at the dial's centre,
forward into the dial, up = the dial's up), built at rest (pointing at the dial's zero) with `n` lines that tell
DriverCam the value range and sweep; the digital panel (the W8) gets a pivot part of quads with `dg` (digits) and `db`
(rpm bars) lines. Ranges come from specs.DIALS, the same table gauges.py prints the faces from.
"""
import sys, os, math, re
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy, bmesh
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree
import dcm_io, shapes
from specs import SPECS, DIALS, SWEEP, NEEDLE_MAT, GLYPH, BAR_TEXELS, DIGITAL, OLD_INTERIOR, TRIM_DEFAULT

HERE = os.path.dirname(os.path.abspath(__file__))
argv = sys.argv[sys.argv.index("--") + 1:]
CAR, OLD, OUTDIR = argv[0], argv[1], argv[2]
PREVIEW = argv[3] if len(argv) > 3 and argv[3] else None
CFG = argv[4] if len(argv) > 4 and argv[4] else None
S = SPECS[CAR]
PAL = dict(S["pal"])
PAL.setdefault("vent", (0.02, 0.02, 0.022, 0.2, 0.0, 0.2))
PAL.setdefault("screen", (0.015, 0.015, 0.02, 0.7, 0.0, 0.2))
PAL.setdefault("wood", PAL["dash"])
PAL.setdefault("door", PAL["console"])
PAL.setdefault("headliner", PAL["door"])
PAL.setdefault("needle", NEEDLE_MAT(DIALS[CAR]["style"]))
DIAL = DIALS[CAR]
TRIM = dict(TRIM_DEFAULT); TRIM.update(S.get("trim", {}))

def read_layout(path):
    for line in open(path, encoding="utf-8"):
        if line.startswith("layout "): return int(line.split()[1])
    return 1

BASE_REV = "d113f22"   # DriverCam 0.9.1: the last commit whose cockpits are the layout-1 auto-fits

def base_cockpit(car):
    """The car's layout-1 auto-fit, read from git at BASE_REV into a scratch file (never written into the repo)."""
    import subprocess, tempfile
    out_dir = os.environ.get("DCM_BASE_DIR") or os.path.join(tempfile.gettempdir(), "dcm_base")
    os.makedirs(out_dir, exist_ok=True)
    dst = os.path.join(out_dir, f"cockpit_{car}.{BASE_REV}.dcm")
    if not os.path.exists(dst):
        spec = f"{BASE_REV}:source/DriverCam/Assets/cockpits/cockpit_{car}.dcm"
        r = subprocess.run(["git", "show", spec], cwd=HERE, capture_output=True)
        if r.returncode != 0: sys.exit(f"[{car}] could not read the layout-1 base ({spec}): {r.stderr.decode(errors='replace').strip()}")
        open(dst, "wb").write(r.stdout.replace(b"\r\n", b"\n"))
    if read_layout(dst) != 1: sys.exit(f"[{car}] {dst} is not a layout-1 auto-fit")
    return dst

if OLD == "base" or read_layout(OLD) >= 2:   # a cockpit this script built: start again from the car's layout-1 auto-fit
    base = base_cockpit(CAR)
    print(f"[{CAR}] {OLD} is a modelled cockpit (layout 2) or 'base': building from the layout-1 auto-fit {base}")
    OLD = base

def game_dir():
    props = os.path.join(HERE, "..", "..", "..", "local.props")
    m = re.search(r"<GameDir>([^<]+)</GameDir>", open(props, encoding="utf-8").read()) if os.path.exists(props) else None
    return m.group(1).strip() if m else None

if CFG == "live":   # the setup the game uses: BepInEx/config first (per-player copy), then the shared copy
    gd = game_dir()
    cands = [os.path.join(gd, "BepInEx", "config", "DriverCam_cars", f"{CAR}.cfg"),
             os.path.join(gd, "BepInEx", "plugins", "DriverCam", "cars", f"{CAR}.cfg")] if gd else []
    CFG = next((c for c in cands if os.path.exists(c)), None)
    if CFG is None: sys.exit(f"[{CAR}] no live car setup found (GameDir from source/local.props: {gd})")
print(f"[{CAR}] car setup: {CFG}")

dcm_io.clear_scene()
old = dcm_io.parse(OLD)
U = dcm_io.u2b

# ------------------------------------------------------------------ the car's hand-tuned DriverCam setup
TUNE = {"Driver.OffsetX": 0.0, "Driver.OffsetY": 0.0, "Driver.OffsetZ": 0.0, "Driver.Pitch": 0.0, "View.Fov": 60.0}
PARTS = {}
if CFG and os.path.exists(CFG):
    for line in open(CFG, encoding="utf-8"):
        if "=" not in line or line.lstrip().startswith("#"): continue
        k, v = (t.strip() for t in line.split("=", 1))
        if k.startswith("Part."): PARTS[k[5:]] = [float(x) for x in v.split()]
        elif k in TUNE:
            try: TUNE[k] = float(v)
            except ValueError: pass
if PARTS.get("Interior", [0, 0, 0, 0, 0, 0, 1]) == [0, 0, 0, 0, 0, 0, 1]:
    PARTS["Interior"] = list(OLD_INTERIOR[CAR])   # migrated setup: the layout-1 dash sat at the old shipped offset
    print(f"[{CAR}] Part.Interior is identity (layout 2): using the layout-1 offset {PARTS['Interior']}")

def rot_u(e):
    """Unity Euler (x, y, z degrees; applied z, then x, then y) as a Blender rotation matrix: Unity x = Blender -X
    rotation, Unity y (yaw) = Blender -Z, Unity z (roll) = Blender -Y."""
    from mathutils import Euler as BE
    x, y, z = (math.radians(a) for a in e)
    return BE((0.0, 0.0, -y)).to_matrix() @ BE((-x, 0.0, 0.0)).to_matrix() @ BE((0.0, -z, 0.0)).to_matrix()

def part_tf(name):
    """Part offset (x y z, euler x y z degrees, scale) as DriverCam applies it around the group's centre."""
    p = PARTS.get(name, [0, 0, 0, 0, 0, 0, 1])
    return Vector(p[0:3]), rot_u(p[3:6]), p[6]

# ------------------------------------------------------------------ reference points from the old cockpit (Blender coords)
eye_u = Vector(old["eye"]) + Vector((TUNE["Driver.OffsetX"], TUNE["Driver.OffsetY"], TUNE["Driver.OffsetZ"]))
E = U(*eye_u)
wheel_part = next(p for p in old["parts"] if p["group"] == "SteeringWheel")
Pp, Pf, Pu = wheel_part["pivot"]
wpos, wrot, wsc = part_tf("SteeringWheel")
P = U(*(Vector(Pp) + wpos)); F = (wrot @ U(*Pf)).normalized(); UP = (wrot @ U(*Pu)).normalized()
R = F.cross(UP).normalized() * -1   # Blender right-handed: right = forward x up ... check below
# right must point to +X for a wheel facing forward: verify and flip
if R.x < 0: R = -R

def verts_of(group=None, name_has=None):
    out = []
    for p in old["parts"]:
        if group and p["group"] != group: continue
        if name_has and name_has not in p["name"]: continue
        if p["pivot"] is not None: continue
        for tris in p["tags"].values():
            for tri in tris:
                for (v, n, uv) in tri: out.append(U(*v))
    return out

pil = verts_of(name_has="APillarL")
ws = min(pil, key=lambda v: v.z)                 # windscreen base at the left pillar foot
ws_y, ws_z = ws.y, ws.z
inter = verts_of(group="Interior")
# where the tuned old dash really appeared: the Interior part offset applied around its group centre
if inter:
    lo = Vector((min(v.x for v in inter), min(v.y for v in inter), min(v.z for v in inter)))
    hi = Vector((max(v.x for v in inter), max(v.y for v in inter), max(v.z for v in inter)))
    cen = (lo + hi) / 2
    ipos, irot, isc = part_tf("Interior")
    ip = U(*ipos)
    inter = [cen + ip + (irot @ ((v - cen) * isc)) for v in inter]
floor = min(v.z for v in inter) + 0.02
HW = 1.55 * abs(E.x)                             # cabin half width at the dash
D = S["dash"]
# the old cockpit's own dash: its top surface ahead of the wheel (97th percentile height) and how far forward it reaches
ahead = sorted(v.z for v in inter if v.y > P.y + 0.15 and abs(v.x) < HW)
dash_old = ahead[int(len(ahead) * 0.97)] if ahead else ws_z
front_pts = [v.y for v in inter if v.z > dash_old - 0.08 and v.y > P.y]
ws_y = max(front_pts) if front_pts else ws_y
ws_z = dash_old
yR = max(ws_y - D["depth"], P.y + 0.20)          # dash face (towards the driver)
zT = min(dash_old + D.get("rise", 0.0), E.z - 0.24)  # dash top
print(f"[{CAR}] eye {tuple(round(c, 3) for c in E)} wheel {tuple(round(c, 3) for c in P)} ws ({ws_y:.2f},{ws_z:.2f}) floor {floor:.2f} hw {HW:.2f} dash face y {yR:.2f} top z {zT:.2f}")

# ------------------------------------------------------------------ the body-fitted parts where the game shows them
def group_T(group):
    """4x4: the group's file coordinates -> where the game shows them (its part offset around its centre)."""
    pts = verts_of(group=group)
    if not pts: return Matrix.Identity(4)
    lo = Vector((min(v.x for v in pts), min(v.y for v in pts), min(v.z for v in pts)))
    hi = Vector((max(v.x for v in pts), max(v.y for v in pts), max(v.z for v in pts)))
    c = (lo + hi) / 2
    gp, grot, gsc = part_tf(group)
    return Matrix.Translation(c + U(*gp)) @ grot.to_4x4() @ Matrix.Scale(gsc, 4) @ Matrix.Translation(-c)

def tuned_group(group, name_has=None):
    """The group's (or one of its parts') vertices where the game shows them."""
    T = group_T(group)
    return [T @ v for v in verts_of(group=group, name_has=name_has)]

# a group the car setup shrank to almost nothing was hidden on purpose: leave it out, build the cabin from the shell
DROP_GROUPS = {g for g in ("Doors",) if g in PARTS and part_tf(g)[2] < 0.35}
for g in DROP_GROUPS: print(f"[{CAR}] Part.{g} scale {part_tf(g)[2]:.2f}: the setup hides it; left out, door cards from the shell")
doors_t = [] if "Doors" in DROP_GROUPS else tuned_group("Doors")
shell_t = tuned_group("Shell")

def side_wall(side):
    """The cabin's side wall beside the seats on one side (-1 left, +1 right), from the tuned front door, or from the
    shell's inner wall when there is no usable door: x_in (inner face), x_out (outer edge of the door top), belt,
    the door top as a line z = a + b * y, the door's front and rear ends and where the info came from."""
    near = lambda v: v.x * side > 0.3 and abs(v.y - (E.y + 0.2)) < 0.9 and floor < v.z < E.z
    dv = [v for v in doors_t if near(v)]; src = "doors"
    if len(dv) < 8:
        dv = [v for v in shell_t if near(v) and v.z > E.z - 0.55]; src = "shell"
    if len(dv) < 8: return None
    xs_in = sorted(abs(v.x) for v in dv)
    x_in = xs_in[int(len(xs_in) * 0.1)] - 0.01
    zs = sorted(v.z for v in dv)
    belt = min(zs[int(len(zs) * 0.92)], E.z - 0.30)
    top = [v for v in dv if v.z > belt - 0.10]
    xs_top = sorted(abs(v.x) for v in top) or [x_in + 0.06]
    x_out = max(xs_top[int(len(xs_top) * 0.9)] if src == "doors" else x_in + 0.06, x_in + 0.04)
    bins = {}
    for v in top: k = round(v.y / 0.1); bins[k] = max(bins.get(k, -9.0), v.z)
    pts = [(k * 0.1, z) for k, z in bins.items()]
    if len(pts) >= 2:
        my = sum(p[0] for p in pts) / len(pts); mz = sum(p[1] for p in pts) / len(pts)
        sxx = sum((p[0] - my) ** 2 for p in pts)
        b = max(-0.15, min(0.15, sum((p[0] - my) * (p[1] - mz) for p in pts) / sxx if sxx > 1e-9 else 0.0))
        a = mz - b * my
        a += max(0.0, max(p[1] - (a + b * p[0]) for p in pts))
    else:
        a, b = belt + 0.03, 0.0
    # the front door part beside the driver: its rear end is the B-pillar
    y_b = None
    if src == "doors":
        for p in old["parts"]:
            if p["group"] != "Doors" or p["pivot"] is not None: continue
            vs = [v for v in tuned_group("Doors", p["name"]) if v.x * side > 0.2]
            if len(vs) > 8 and min(v.y for v in vs) < E.y + 0.1 < max(v.y for v in vs):
                y_b = min(v.y for v in vs)
    if y_b is None or not (E.y - 0.45 <= y_b <= E.y + 0.05): y_b = E.y - 0.25
    return dict(src=src, x_in=x_in, x_out=x_out, belt=belt, top_a=a, top_b=b, y_front=max(v.y for v in dv), y_b=y_b)

SIDE = {s: side_wall(s) for s in (-1, 1)}
for s in (-1, 1):
    if SIDE[s] is None:   # nothing fitted on this side: mirror the other side, or a plain wall beside the seat
        o = SIDE[-s]
        SIDE[s] = dict(o) if o else dict(src="none", x_in=abs(E.x) + 0.32, x_out=abs(E.x) + 0.40, belt=E.z - 0.30,
                                         top_a=E.z - 0.27, top_b=0.0, y_front=yR + 0.3, y_b=E.y - 0.25)
    w = SIDE[s]
    # the wall must clear the seats (both are as wide as the driver's, at +-eye x): a door piece the setup moved into the
    # cabin stays behind the card and its inner parts are clipped (see CLIP below)
    w["x_in"] = max(w["x_in"], abs(E.x) + 0.30)
    w["x_out"] = min(max(w["x_out"], w["x_in"] + 0.06), w["x_in"] + TRIM["sill"])   # the sill (cap) is never wider than the car's
for s, w in SIDE.items():
    if w: print(f"[{CAR}] side {s:+d} ({w['src']}): x_in {w['x_in']:.3f} x_out {w['x_out']:.3f} belt {w['belt']:.3f} top z {w['top_a']:.3f}{w['top_b']:+.3f}*y front y {w['y_front']:.2f} B-pillar y {w['y_b']:.2f} (eye y {E.y:.2f}, eye x {E.x:.3f})")

OBJS = []   # (obj, tag, group, smooth, textured, pivot); pivot: False, True (the wheel) or a gauge part dict

def finish(name, bm, tag, smooth=False, textured=False, group="Interior", pivot=False):
    for f in bm.faces: f.smooth = smooth
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    me.materials.append(dcm_io.material(tag, PAL.get(tag, (0.5, 0.5, 0.5))[:3]))
    ob = bpy.data.objects.new(name, me); bpy.context.scene.collection.objects.link(ob)
    OBJS.append((ob, tag, group, smooth, textured, pivot))
    return ob

def box(name, center, size, tag, rot=Matrix.Identity(3), bevel=0.008, segs=1, smooth=False, group="Interior", pivot=False):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=Vector(size), verts=bm.verts)
    if bevel > 0:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=min(bevel, min(size) * 0.45), segments=segs, affect="EDGES", profile=0.5)
    bmesh.ops.transform(bm, matrix=rot.to_4x4(), verts=bm.verts)
    bmesh.ops.translate(bm, vec=Vector(center), verts=bm.verts)
    return finish(name, bm, tag, smooth or bevel > 0.02, group=group, pivot=pivot)

def basis(n, up_hint=Vector((0, 0, 1))):
    n = n.normalized(); r = up_hint.cross(n).normalized(); u = n.cross(r).normalized()
    return r, u, n   # right, up, normal (towards the viewer)

def mat_from(r, u, n): return Matrix((r, n * -1, u)).transposed()  # local x=right, y=away from viewer, z=up

def cylinder(name, a, b, r, tag, segs=12, cap=True, smooth=True, group="Interior", pivot=False):
    axis = (b - a); L = axis.length; axis.normalize()
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=cap, cap_tris=False, segments=segs, radius1=r, radius2=r, depth=L)
    q = Vector((0, 0, 1)).rotation_difference(axis)
    bmesh.ops.rotate(bm, verts=bm.verts, cent=Vector(), matrix=q.to_matrix())
    bmesh.ops.translate(bm, vec=(a + b) * 0.5, verts=bm.verts)
    return finish(name, bm, tag, smooth, group=group, pivot=pivot)

def sphere(name, c, r, tag, group="Interior", pivot=False):
    bm = bmesh.new(); bmesh.ops.create_uvsphere(bm, u_segments=10, v_segments=6, radius=r)
    bmesh.ops.translate(bm, vec=c, verts=bm.verts)
    return finish(name, bm, tag, True, group=group, pivot=pivot)

def cone(name, a, b, r1, r2, tag, segs=10):
    axis = (b - a); L = axis.length; axis.normalize()
    bm = bmesh.new(); bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segs, radius1=r1, radius2=r2, depth=L)
    q = Vector((0, 0, 1)).rotation_difference(axis); bmesh.ops.rotate(bm, verts=bm.verts, cent=Vector(), matrix=q.to_matrix())
    bmesh.ops.translate(bm, vec=(a + b) * 0.5, verts=bm.verts)
    return finish(name, bm, tag, True)

def disc(name, c, r_, u_, n, rad, tag, cell=None, segs=18, rect=None):
    """Gauge face facing the viewer (n); UVs into the 4x2 atlas cell. rect=(w,h) makes a rectangle (digital panel)."""
    bm = bmesh.new(); uvl = bm.loops.layers.uv.new("UVMap")
    if cell is not None:
        cu, cv = ((cell % 4) + 0.5) / 4.0, 1.0 - ((cell // 4) + 0.5) / 2.0
    pts, uvs = [], []
    if rect:
        w, h = rect
        for (sx, sy) in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            pts.append(c + r_ * (sx * w / 2) + u_ * (sy * h / 2)); uvs.append((cu + sx * 0.125 * 0.97, cv + sy * 0.25 * 0.6))
    else:
        for k in range(segs):
            a = 2 * math.pi * k / segs
            pts.append(c + (r_ * math.cos(a) + u_ * math.sin(a)) * rad)
            uvs.append((cu + math.cos(a) * 0.125 * 0.975, cv + math.sin(a) * 0.25 * 0.975) if cell is not None else (0, 0))
    vs = [bm.verts.new(p) for p in pts]
    f = bm.faces.new(vs)
    for loop, uvv in zip(f.loops, uvs): loop[uvl].uv = uvv
    return finish(name, bm, tag, False, textured=cell is not None)

def ring(name, c, r_, u_, n, r_out, r_in, depth, tag, segs=16):
    """Bezel: a lip standing out of the panel towards the viewer (inner edge, outer edge, base)."""
    bm = bmesh.new()
    rings = []
    for (rad, off) in ((r_in, depth), (r_out, depth), (r_out, -0.002)):
        rings.append([bm.verts.new(c + n * off + (r_ * math.cos(2 * math.pi * k / segs) + u_ * math.sin(2 * math.pi * k / segs)) * rad) for k in range(segs)])
    for i in range(2):
        A, B = rings[i], rings[i + 1]
        for k in range(segs):
            try: bm.faces.new((A[k], A[(k + 1) % segs], B[(k + 1) % segs], B[k]))
            except ValueError: pass
    # outward: the lip's faces point away from the dial's axis and towards the viewer (the game culls back faces)
    for f in bm.faces:
        cf = f.calc_center_median() - c
        radial = cf - n * cf.dot(n)
        if f.normal.dot(radial) + f.normal.dot(n) * 0.5 < 0: f.normal_flip()
    return finish(name, bm, tag, True)

def panel(name, c, r_, u_, n, w, h, thick, tag, bevel=0.006):
    M = mat_from(r_, u_, n)
    return box(name, c - n * (thick / 2), (w, thick, h), tag, rot=M, bevel=bevel)

# ------------------------------------------------------------------ dashboard (a profile swept across the cabin)
def dash_profile(x):
    """Closed (y, z) profile at cabin x: top-front under the glass, top towards the driver, face, knee, floor, firewall."""
    wrap = D.get("wrap", 0.0)
    w = 0.0
    if wrap:   # cockpit wrap: the dash comes towards the driver around his side (RX-7, W8)
        t = (x - E.x) / 0.45
        w = wrap * max(0.0, 1.0 - t * t)
    cowl = 0.0
    if D.get("twin_cowl"):   # '69 Mustang: humps over the driver and the passenger
        for cx in (E.x, -E.x):
            t = (x - cx) / 0.32
            cowl = max(cowl, 0.045 * max(0.0, 1.0 - t * t))
    y_r = yR - w; z_t = zT + cowl
    return [(ws_y + 0.03, ws_z - 0.01), (y_r + 0.05, z_t), (y_r, z_t - 0.04), (y_r + 0.05, z_t - 0.14),
            (y_r + 0.17, floor + 0.42), (y_r + 0.45, floor + 0.04), (ws_y + 0.2, floor + 0.04), (ws_y + 0.2, ws_z - 0.25)]

def dashboard():
    bm = bmesh.new()
    N = 16 if (D.get("wrap") or D.get("twin_cowl")) else 6   # only curved dashes need many slices
    xL, xR = -(SIDE[-1]["x_in"] - 0.002), SIDE[1]["x_in"] - 0.002   # door card to door card: no gap at the dash ends
    xs = [xL + (xR - xL) * i / (N - 1) for i in range(N)]
    rings = []
    for x in xs:
        rings.append([bm.verts.new((x, y, z)) for (y, z) in dash_profile(x)])
    m = len(rings[0])
    for i in range(N - 1):
        A, B = rings[i], rings[i + 1]
        for k in range(m):
            bm.faces.new((A[k], B[k], B[(k + 1) % m], A[(k + 1) % m]))
    bm.faces.new(list(reversed(rings[0]))); bm.faces.new(rings[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    finish("RL_Dash", bm, "dash", smooth=False)
    # soft-touch top pad (a slightly darker skin on the top surface)
    bm = bmesh.new(); top = []
    for x in xs:
        pr = dash_profile(x)
        top.append([bm.verts.new((x, pr[0][0] - 0.01, pr[0][1] + 0.004)), bm.verts.new((x, pr[1][0] + 0.005, pr[1][1] + 0.004))])
    for i in range(N - 1):
        bm.faces.new((top[i][0], top[i + 1][0], top[i + 1][1], top[i][1]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    for f in bm.faces:
        if f.normal.z < 0: f.normal_flip()
    finish("RL_DashTop", bm, "dash_top")

dashboard()

# ------------------------------------------------------------------ working gauges (pivot parts DriverCam turns)
def gauge_pivot(name, c, r_, u_, n, lines, lift):
    """A gauge part's pivot: on the dial's centre, `lift` metres towards the eye; local x = r_, y = into the dial, z = u_."""
    return dict(part=name, P=c + n * lift, F=-n, U=u_, R=r_, lines=lines)

def flat_face(bm, pts, uvs=None, uvl=None):
    """One polygon facing the eye (-y in pivot space; the game culls back faces), with optional per-corner UVs."""
    vs = [bm.verts.new(p) for p in pts]
    f = bm.faces.new(vs)
    f.normal_update()
    if f.normal.y > 0: f.normal_flip()
    if uvs is not None:
        by_v = {v: uv for v, uv in zip(vs, uvs)}
        for loop in f.loops: loop[uvl].uv = by_v[loop.vert]
    return f

def needle(name, c, r_, u_, n, rad, lines, a0):
    """A dial needle at rest (pointing at angle a0, maths degrees in the dial plane), tapered, with a hub cap. Built in
    pivot space: x right, y into the dial, z up; DriverCam turns it about y from its `n` lines."""
    pv = gauge_pivot(name, c, r_, u_, n, lines, 0.004)
    a = math.radians(a0)
    d = Vector((math.cos(a), 0.0, math.sin(a))); s = Vector((-math.sin(a), 0.0, math.cos(a)))
    w0, wt, w1 = 0.05 * rad, 0.035 * rad, 0.014 * rad   # root, tail and tip widths
    tail, tip = -0.14 * rad, 0.80 * rad
    bm = bmesh.new()
    flat_face(bm, [d * tail - s * (wt / 2), -s * (w0 / 2), d * tip - s * (w1 / 2), d * tip + s * (w1 / 2), s * (w0 / 2), d * tail + s * (wt / 2)])
    finish(name + "_Blade", bm, "needle", pivot=pv)
    bm = bmesh.new()   # hub cap, just in front of the blade
    flat_face(bm, [Vector((math.cos(2 * math.pi * k / 8), 0.0, math.sin(2 * math.pi * k / 8))) * (0.09 * rad) + Vector((0.0, -0.001, 0.0)) for k in range(8)])
    finish(name + "_Cap", bm, "bezel", pivot=pv)

def dial_needle(name, cell, c, r_, u_, n, rad):
    """Speedometer (cell 0: one range per unit, DriverCam picks the game's) and tachometer (cell 1) needles."""
    a0, a1 = SWEEP
    if DIAL["style"] == "digital": return
    if cell == 0:
        needle(name, c, r_, u_, n, rad, [f"n speed kmh 0 {DIAL['kmh'][0]:g} {a0:g} {a1:g}", f"n speed mph 0 {DIAL['mph'][0]:g} {a0:g} {a1:g}"], a0)
    elif cell == 1:
        tmax, red = DIAL["tach"]
        needle(name, c, r_, u_, n, rad, [f"n rpm - 0 {tmax * 1000:g} {a0:g} {a1:g} {red * 1000:g}"], a0)

def digital_readout(name, c, r_, u_, n, w, h):
    """The W8's screen: digit quads (UVs on glyph 0) then the rpm bar quads (UVs on the unlit patch), one pivot part.
    Positions in specs.DIGITAL's normalised panel coordinates, the same ones gauges.py prints the unit label at."""
    D_ = DIGITAL
    tmax, red = DIAL["tach"]
    g, b = GLYPH, BAR_TEXELS
    W, H = 1024.0, 512.0
    gu0, gv0 = (g["x0"] + 1) / W, 1.0 - (g["y0"] + g["h"] - 1) / H
    gu1, gv1 = (g["x0"] + g["w"] - 1) / W, 1.0 - (g["y0"] + 1) / H
    du = g["pitch"] / W
    bu0, bv0 = (b["x0"] + 16) / W, 1.0 - (b["y0"] + b["h"] - 16) / H
    bu1, bv1 = (b["x0"] + b["w"] - 16) / W, 1.0 - (b["y0"] + 16) / H
    bdu = b["pitch"] / W
    lines = [f"dg speed - {D_['digits']} {gu0:.6f} {gv0:.6f} {du:.6f}",
             f"db rpm {D_['bars']} 0 {tmax * 1000:g} {red * 1000:g} {bdu:.6f} 0 {2 * bdu:.6f} 0"]
    pv = gauge_pivot(name, c, r_, u_, n, lines, 0.003)
    P = lambda x, y: Vector((x * w / 2, 0.0, y * h / 2))
    bm = bmesh.new(); uvl = bm.loops.layers.uv.new("UVMap")
    gh = D_["digit_h"]; gw = gh * (h / 2) * (g["w"] / g["h"]) / (w / 2)   # glyph width in normalised x, keeping its aspect
    for k in range(D_["digits"]):
        x0 = D_["digit_x0"] + k * gw * D_["digit_gap"]; y0 = D_["digit_y0"]
        flat_face(bm, [P(x0, y0), P(x0 + gw, y0), P(x0 + gw, y0 + gh), P(x0, y0 + gh)], [(gu0, gv0), (gu1, gv0), (gu1, gv1), (gu0, gv1)], uvl)
    nb = D_["bars"]; pitch = (D_["bar_x1"] - D_["bar_x0"]) / nb
    for k in range(nb):
        x0 = D_["bar_x0"] + k * pitch; x1 = x0 + pitch * D_["bar_fill"]
        y0 = D_["bar_y0"]; y1 = y0 + D_["bar_h0"] + (D_["bar_h1"] - D_["bar_h0"]) * k / max(1, nb - 1)
        flat_face(bm, [P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1)], [(bu0, bv0), (bu1, bv0), (bu1, bv1), (bu0, bv1)], uvl)
    finish(name, bm, "gauges", textured=True, pivot=pv)

# ------------------------------------------------------------------ instrument cluster
CW = S["cluster_w"]
yface = yR - D.get("wrap", 0.0) - 0.03   # mounted on (just proud of) the dash face, under the hood
# aim from the eye through the upper opening of the steering wheel: the dials are always seen through the wheel
_wp = P + UP * ((S["wheel"]["hub_r"] + S["wheel"]["r"] - S["wheel"]["t"]) / 2)   # middle of the gap between hub and rim
_dir = (_wp - E).normalized()
G = E + _dir * ((yface - E.y) / _dir.y)
pod_depth = 0.0
if G.z > E.z - 0.12:
    # low seat / high wheel: bring the cluster out towards the wheel in a deep pod until it sits in the visible gap
    for _ in range(12):
        if G.z <= E.z - 0.12 or yface - 0.03 < P.y + 0.12: break
        yface -= 0.03; pod_depth += 0.03
        G = E + _dir * ((yface - E.y) / _dir.y)
G.z = min(G.z, E.z - 0.12)   # never into the road view   # in the opening between the hub and the top of the rim, as in the real car
r_, u_, n = basis(E - G)
gs = S["gauges"]
gx0 = min(dx - rr for (_, dx, dy, rr) in gs); gx1 = max(dx + rr for (_, dx, dy, rr) in gs)
gy0 = min(dy - rr for (_, dx, dy, rr) in gs); gy1 = max(dy + rr for (_, dx, dy, rr) in gs)
pw, ph = (gx1 - gx0) + 0.05, (gy1 - gy0) + 0.04
pc = G + r_ * ((gx0 + gx1) / 2) + u_ * ((gy0 + gy1) / 2)
style = S["binnacle"]
panel("RL_ClusterPanel", pc - n * 0.004, r_, u_, n, pw, ph, 0.02, "dash_top", bevel=0.004)
if pod_depth > 0.0:   # the pod's body, from the cluster back to the dash face
    box("RL_Pod", pc - n * (0.02 + pod_depth / 2) - u_ * 0.0, (pw + 0.05, pod_depth + 0.02, ph + 0.04), "dash_top",
        rot=mat_from(r_, u_, n), bevel=0.02)
for i, (cell, dx, dy, rad) in enumerate(gs):
    c = G + r_ * dx + u_ * dy
    if cell == 7:
        disc(f"RL_Gauge{i}", c + n * 0.002, r_, u_, n, rad, "gauges", cell=7, rect=(rad * 2.6, rad * 1.1))
        panel("RL_ScreenFrame", c - n * 0.001, r_, u_, n, rad * 2.8, rad * 1.3, 0.01, "bezel", bevel=0.003)
        digital_readout("RL_Digital", c, r_, u_, n, rad * 2.6, rad * 1.1)
        continue
    disc(f"RL_Gauge{i}", c + n * 0.002, r_, u_, n, rad, "gauges", cell=cell)
    ring(f"RL_Bezel{i}", c, r_, u_, n, rad * 1.1, rad * 0.98, 0.012 if style != "pods" else 0.045, "bezel")
    dial_needle(f"RL_Needle{i}", cell, c, r_, u_, n, rad)
# the hood over the cluster
hw_c, hh = pw / 2 + 0.03, ph / 2 + 0.03
if style in ("hood", "pods"):
    bm = bmesh.new(); arcs = []
    segs = 10
    for j, off in enumerate((-0.01, 0.12)):
        pts = []
        for k in range(segs + 1):
            a = math.pi * k / segs
            pts.append(bm.verts.new(pc + n * off + r_ * (math.cos(a) * hw_c) + u_ * (math.sin(a) * hh * 0.9 + ph * 0.05) - u_ * 0.0))
        arcs.append(pts)
    # thickness: a second, inner arc set
    inner = []
    for j, off in enumerate((-0.01, 0.12)):
        inner.append([bm.verts.new(pc + n * off + r_ * (math.cos(math.pi * k / segs) * (hw_c - 0.012)) + u_ * (math.sin(math.pi * k / segs) * (hh * 0.9 - 0.012) + ph * 0.05)) for k in range(segs + 1)])
    for k in range(segs):
        for (A, B) in ((arcs[0], arcs[1]), (inner[1], inner[0])):
            bm.faces.new((A[k], A[k + 1], B[k + 1], B[k]))
        bm.faces.new((arcs[1][k], arcs[1][k + 1], inner[1][k + 1], inner[1][k]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    finish("RL_Hood", bm, "dash_top", smooth=True)
else:   # box / row / digital: a flat visor and cheeks
    vis_c = pc + u_ * (ph / 2 + 0.012) + n * 0.03   # shallow: never between the driver's eye and the dials
    panel("RL_Visor", vis_c, r_, n, -u_, pw + 0.06, 0.065, 0.016, "dash_top", bevel=0.005)
    for s in (-1, 1):
        panel(f"RL_Cheek{s}", pc + r_ * (s * (pw / 2 + 0.02)) + n * 0.03, n, u_, r_ * s, 0.065, ph + 0.03, 0.014, "dash_top", bevel=0.004)

# ------------------------------------------------------------------ centre stack and console
ST = S["stack"]; CO = S["console"]
cw = CO["w"]; ch = CO["h"]
stack_top = Vector((0.0, yR + 0.03, zT - 0.03))
stack_bot = Vector((0.0, yR - 0.12, floor + ch))      # the stack leans back towards the cabin and meets the console
sh = (stack_top - stack_bot).length
s_up = (stack_top - stack_bot).normalized()
s_n = Vector((1, 0, 0)).cross(s_up).normalized()     # faces the cabin: backwards and a little up
ang = math.radians(ST.get("angle", 0))
s_n = (Matrix.Rotation(-ang, 3, s_up) @ s_n).normalized()   # turned towards the driver (on the left)
s_r = s_up.cross(s_n).normalized()
s_c = (stack_top + stack_bot) / 2
panel("RL_Stack", s_c, s_r, s_up, s_n, cw, sh, 0.05, "console", bevel=0.01)
def on_stack(dx, dy): return s_c + s_r * dx + s_up * dy + s_n * 0.002
yy = sh / 2 - 0.05
vents = ST.get("vents", "none")
if vents == "rect2":
    for s in (-1, 1):
        vc = on_stack(s * cw * 0.24, yy - 0.02)
        panel(f"RL_Vent{s}", vc, s_r, s_up, s_n, cw * 0.4, 0.07, 0.01, "vent", bevel=0.003)
        for k in range(3):
            panel(f"RL_Slat{s}_{k}", vc + s_up * (-0.024 + k * 0.016) + s_n * 0.006, s_r, s_up, s_n, cw * 0.38, 0.004, 0.006, "bezel", bevel=0.0)
    yy -= 0.11
elif vents == "round2":
    for s in (-1, 1):
        vc = on_stack(s * cw * 0.25, yy - 0.02)
        ring(f"RL_VentR{s}", vc, s_r, s_up, s_n, 0.04, 0.03, 0.015, "chrome")
        disc(f"RL_VentF{s}", vc - s_n * 0.004, s_r, s_up, s_n, 0.032, "vent")
    yy -= 0.11
for i, (cell, dx, dy, rad) in enumerate(ST.get("gauges", [])):
    gc = on_stack(dx, yy + dy - 0.03)
    disc(f"RL_SGauge{i}", gc + s_n * 0.003, s_r, s_up, s_n, rad, "gauges", cell=cell)
    ring(f"RL_SBezel{i}", gc, s_r, s_up, s_n, rad * 1.12, rad * 0.98, 0.01, "bezel")
    dial_needle(f"RL_SNeedle{i}", cell, gc + s_n * 0.001, s_r, s_up, s_n, rad)
if ST.get("gauges"):
    yy -= (max(dy for (_, _, dy, _) in ST["gauges"]) - min(dy for (_, _, dy, _) in ST["gauges"])) + 0.08
if ST.get("toggles"):
    for k in range(ST["toggles"]):
        tc = on_stack(-cw * 0.4 + k * cw * 0.8 / max(1, ST["toggles"] - 1), yy)
        cylinder(f"RL_Toggle{k}", tc, tc + s_n * 0.03 + s_up * 0.008, 0.004, "chrome", segs=8)
    yy -= 0.05
if ST.get("radio"):
    rc = on_stack(0, yy - 0.03)
    panel("RL_Radio", rc, s_r, s_up, s_n, cw * 0.82, 0.055, 0.012, "screen", bevel=0.003)
    for k in range(4):
        panel(f"RL_RadioBtn{k}", rc + s_r * (-cw * 0.3 + k * cw * 0.15) - s_up * 0.016 + s_n * 0.007, s_r, s_up, s_n, 0.018, 0.01, 0.005, "bezel", bevel=0.0)
    yy -= 0.09
for k in range(ST.get("knobs", 0)):
    kc = on_stack(-cw * 0.3 + k * (cw * 0.6 / max(1, ST["knobs"] - 1)), yy - 0.02)
    cylinder(f"RL_Knob{k}", kc, kc + s_n * 0.022, 0.016, "knob", segs=18)

# ------------------------------------------------------------------ the cabin's shape: side walls, roof, rear
XW = {s: SIDE[s]["x_in"] for s in (-1, 1)}     # inner faces of the side walls (door cards, rear side panels)
XC = (XW[1] - XW[-1]) / 2                       # the cabin's middle between them
DS = -1 if E.x < 0 else 1                       # the driver's side
REAR = TRIM["rear"]                             # "2+2" | "hatch" | "bulkhead"

def cap_top(side, y):
    """Top of the door-top cap at y: just over the tuned door's top edge, never far above the belt."""
    w = SIDE[side]
    return min(max(w["top_a"] + w["top_b"] * y + 0.008, w["belt"] + 0.035), w["belt"] + 0.13)

# the tuned roof slab: its underside's corners where the game shows it, the header in front of it
roof_v = verts_of(group="Roof", name_has="RoofLiner")
TR = group_T("Roof")
if roof_v:
    r_lo = Vector((min(v.x for v in roof_v), min(v.y for v in roof_v), min(v.z for v in roof_v)))
    r_hi = Vector((max(v.x for v in roof_v), max(v.y for v in roof_v), max(v.z for v in roof_v)))
else:   # no roof in the auto-fit: a flat one over the seats
    r_lo = Vector((-XW[-1], E.y - 1.0, E.z + 0.17)); r_hi = Vector((XW[1], yR, E.z + 0.2)); TR = Matrix.Identity(4)
hdr_v = verts_of(group="Roof", name_has="Header")
hdr_front = max((v.y for v in hdr_v), default=r_hi.y)          # file space
hdr_bot = min((v.z for v in hdr_v), default=r_lo.z - 0.04)
def roof_pt(fx, yf, dz=0.0):
    """A point on the roof underside: fx 0 left .. 1 right, yf a file-space y, dz a drop in world z."""
    return TR @ Vector((r_lo.x + (r_hi.x - r_lo.x) * fx, yf, r_lo.z)) + Vector((0, 0, dz))
zr = roof_pt(0.5, (r_lo.y + r_hi.y) / 2).z
z_hdr = (TR @ Vector(((r_lo.x + r_hi.x) / 2, hdr_front, hdr_bot))).z
y_at = lambda yf: roof_pt(0.5, yf).y
def file_y(y):   # the roof's file y whose world y (at the middle) is y
    a, b = y_at(r_lo.y), y_at(r_hi.y)
    return r_lo.y + (y - a) / (b - a) * (r_hi.y - r_lo.y) if abs(b - a) > 1e-6 else r_lo.y
Z_RAIL = zr - 0.012 - 0.055                     # the headliner's side edges (the top of the side windows)
y_bh = E.y - 0.62                               # bulkhead / cargo wall behind the seats (two-seaters)
Y0 = y_at(r_lo.y)                               # the roof's rear edge (world y)
y_cap_front = {}
for s in (-1, 1):   # the door cap runs forward to the A-pillar's foot
    pv = tuned_group("Pillars", "APillarL" if s < 0 else "APillarR")
    y_cap_front[s] = min(max(v.y for v in sorted(pv, key=lambda v: v.z)[:max(1, len(pv) // 4)]) + 0.04, ws_y + 0.15) if pv else yR + 0.3
if REAR == "bulkhead":
    Y_END = y_bh                                # the side walls end at the bulkhead
    Z_PS = E.z - 0.20                           # the ledge under the rear window
    y_rw = y_bh
else:
    # rear window: from the roof's rear edge down to the parcel shelf at the rear cap height, at the car's angle
    Z_PS = max(cap_top(s, E.y - 1.0) for s in (-1, 1)) + 0.005
    Z_PS = min(max(Z_PS, floor + 0.45), Z_RAIL - 0.22)
    y_rw = Y0 - (Z_RAIL - Z_PS) / math.tan(math.radians(TRIM["slope"]))
    Y_END = y_rw
print(f"[{CAR}] cabin: walls x {-XW[-1]:.3f}..{XW[1]:.3f}, roof z {zr:.3f} rail {Z_RAIL:.3f}, roof y {Y0:.2f}..{y_at(r_hi.y):.2f}, rear '{REAR}' ends y {Y_END:.2f}, shelf z {Z_PS:.3f}")

# ------------------------------------------------------------------ tunnel / console between the seats
c_y0, c_y1 = stack_bot.y + 0.02, E.y - 0.10            # from under the stack back past the seat fronts
prof = [(-cw / 2 - 0.045, 0.0), (cw / 2 + 0.045, 0.0), (cw / 2, ch - 0.03), (cw / 2 - 0.03, ch), (-cw / 2 + 0.03, ch), (-cw / 2, ch - 0.03)]
finish("RL_Console", shapes.loft([[Vector((x, y, floor + z)) for (x, z) in prof] for y in (c_y1, c_y0)]), "console")
if CO.get("high"):   # tall console tops (Countach, DeLorean, C3, W8): an armrest pad
    box("RL_Armrest", Vector((0.0, E.y - 0.05, floor + ch + 0.035)), (cw * 0.85, 0.32, 0.07), "seat", bevel=0.025, segs=2)
if CO.get("wood"):
    box("RL_ConsoleWood", Vector((0.0, (c_y0 + c_y1) / 2, floor + ch + 0.002)), (cw * 0.6, abs(c_y0 - c_y1) * 0.8, 0.006), "wood", bevel=0.0)

# shifter
SH = S["shifter"]
SEAT_W = {s: 0.50 for s in (-1, 1)}
if SH["x"] == "center":
    sx = 0.0
else:   # a sill shifter (the W8): in the gap between the driver's seat and the door card, the seat narrowed to fit
    pod_w = 0.08
    sx = DS * (XW[DS] - 0.012 - pod_w / 2)
    SEAT_W[DS] = min(0.50, 2 * (XW[DS] - abs(E.x) - pod_w - 0.025))
sbase = Vector((sx, E.y + 0.36, floor + (ch if SH["x"] == "center" else 0.45)))
if SH["x"] != "center":
    box("RL_ShiftPod", sbase - Vector((0, 0, 0.12)), (pod_w, 0.25, 0.24), "console", bevel=0.02)
lever_h = {"short": 0.12, "tall": 0.24, "gated": 0.20}[SH["style"]]
tip = sbase + Vector((0, -0.02, lever_h))
if SH["style"] == "gated":
    box("RL_Gate", sbase + Vector((0, 0, 0.004)), (0.11, 0.16, 0.008), "chrome", bevel=0.002)
    for k in range(3):
        box(f"RL_GateSlot{k}", sbase + Vector((-0.035 + k * 0.035, 0, 0.0085)), (0.008, 0.11, 0.002), "vent", bevel=0.0)
else:
    cone("RL_Boot", sbase, sbase + Vector((0, -0.005, 0.06)), 0.055, 0.015, "seat" if SH["style"] == "tall" else "knob")
cylinder("RL_Lever", sbase, tip, 0.008 if SH["chrome"] else 0.011, "chrome" if SH["chrome"] else "knob", segs=10)
sphere("RL_ShiftKnob", tip, 0.026 if SH["style"] != "short" else 0.028, "knob" if SH["style"] != "gated" else "chrome")

# ------------------------------------------------------------------ seats
def seat(name, cx, low=False, style="bucket", simple=False, w=0.50):
    cz = E.z - (0.80 if low else 0.74)
    cy = E.y + 0.08
    tilt = math.radians(26 if low else 16)
    box(f"{name}_Cushion", Vector((cx, cy, cz - 0.06)), (w, 0.50, 0.12), "seat", bevel=0.035, segs=2)
    if not simple: box(f"{name}_CushionInsert", Vector((cx, cy + 0.02, cz + 0.0)), (w * 0.55, 0.40, 0.02), "seat_insert", bevel=0.008)
    back_h = 0.50 if style == "classic" else 0.64
    Rb = Matrix.Rotation(tilt, 3, "X")   # positive: the top leans back (-Y)
    bc = Vector((cx, cy - 0.27, cz)) + Rb @ Vector((0, 0, back_h / 2))
    box(f"{name}_Back", bc, (w * 0.96, 0.12, back_h), "seat", rot=Rb, bevel=0.035, segs=2)
    # the hard shell on the seat's back (seen from behind, over the shoulder and from the rear seats)
    box(f"{name}_BackPanel", bc + Rb @ Vector((0, -0.064, -0.01)), (w * 0.80, 0.012, back_h * 0.84), "console", rot=Rb, bevel=0.0)
    if simple:
        style = "classic" if style == "classic" else "simple"
    else:
        box(f"{name}_BackInsert", bc + Rb @ Vector((0, 0.065, -0.02)), (w * 0.52, 0.02, back_h * 0.7), "seat_insert", rot=Rb, bevel=0.008)
    if style == "bucket" or style == "low":
        for s in (-1, 1):
            box(f"{name}_Bolster{s}", bc + Rb @ Vector((s * (w * 0.5 - 0.03), 0.05, -0.05)), (0.08, 0.12, back_h * 0.75), "seat", rot=Rb, bevel=0.03, segs=2)
            box(f"{name}_CBolster{s}", Vector((cx + s * (w * 0.5 - 0.035), cy, cz + 0.01)), (0.07, 0.46, 0.06), "seat", bevel=0.025, segs=2)
    if style != "classic":
        hc = Vector((cx, cy - 0.27, cz)) + Rb @ Vector((0, 0, back_h + 0.10))
        box(f"{name}_Headrest", hc, (0.26, 0.10, 0.18), "seat", rot=Rb, bevel=0.035, segs=2)

style = S["seats"]
seat("RL_SeatDriver", E.x, low=style == "low", style=style, simple=True, w=SEAT_W[DS])
seat("RL_SeatPassenger", -E.x, low=style == "low", style=style, w=SEAT_W[-DS])
RZ, RY = floor + 0.30, E.y - 0.95                # rear seat cushion height and position
RT = math.radians(28)                           # rear backrest lean
RB_SINK = 0.04                                  # how far the rear backrests reach down into the cushions
if S["rear"]:   # 2+2: two shaped rear seats with a hump between them, the backrest up to the parcel shelf
    xr = min(abs(E.x) * 0.85, min(XW.values()) - 0.26)
    wr = min(0.46, 2 * (min(XW.values()) - xr - 0.03))
    Rr = Matrix.Rotation(RT, 3, "X")
    rback_h = max(0.44, (Z_PS - RZ) / math.cos(RT) + 0.02)
    for s in (-1, 1):
        cx = XC + s * xr
        box(f"RL_RearCushion{s}", Vector((cx, RY, RZ - 0.05)), (wr, 0.44, 0.11), "seat", bevel=0.03)
        box(f"RL_RearCushionInsert{s}", Vector((cx, RY + 0.01, RZ + 0.008)), (wr * 0.6, 0.36, 0.02), "seat_insert", bevel=0.0)
        # the backrest's foot runs RB_SINK down its own axis into the cushion (no see-through slit in the crease);
        # its top stays where it was
        bc = Vector((cx, RY - 0.24, RZ)) + Rr @ Vector((0, 0, (rback_h - RB_SINK) / 2))
        box(f"RL_RearBack{s}", bc, (wr, 0.10, rback_h + RB_SINK), "seat", rot=Rr, bevel=0.03)
        box(f"RL_RearBackInsert{s}", bc + Rr @ Vector((0, 0.052, -0.03 + RB_SINK / 2)), (wr * 0.56, 0.012, rback_h * 0.6), "seat_insert", rot=Rr, bevel=0.0)
        # quarter trim: closes the gap from the backrest's outer edge to the side wall (else the eye looks past the
        # seat into the open space under the shelf and out of the car)
        xe = cx + s * wr / 2 - s * 0.03               # 3 cm into the seat (its bevelled edge)
        xw = s * (XW[s] + 0.015)                      # into the side panel
        if s * (xw - xe) > 0.04:
            qc = Vector(((xe + xw) / 2, RY - 0.24, RZ)) + Rr @ Vector((0, -0.015, (rback_h - RB_SINK) / 2))
            box(f"RL_RearQuarter{s}", qc, (abs(xw - xe), 0.06, rback_h + RB_SINK), "door", rot=Rr, bevel=0.01)
    mid_w = 2 * xr - wr + 0.02
    if mid_w > 0.05:
        box("RL_RearMiddle", Vector((XC, RY - 0.02, RZ - 0.06)), (mid_w, 0.42, 0.09), "seat", bevel=0.02)
        box("RL_RearMiddleBack", Vector((XC, RY - 0.26, RZ)) + Rr @ Vector((0, 0, (rback_h - RB_SINK) / 2)), (mid_w, 0.08, rback_h - 0.02 + RB_SINK), "seat", rot=Rr, bevel=0.02)
    # the backrest's top meets the shelf: everything behind it is the boot
    Y_SHELF = RY - 0.24 - math.sin(RT) * rback_h - 0.04
    # the boot wall behind the backrests, floor to shelf: whatever slips past a seat ends on trim, never outside
    box("RL_RearBootWall", Vector((XC, Y_SHELF - 0.015, (floor + Z_PS) / 2 - 0.01)), (XW[-1] + XW[1] + 0.03, 0.03, Z_PS - floor - 0.02), "carpet", bevel=0.005)

# carpet: the floor, rising at the front into a toe board under the dash
bm = bmesh.new()
y1c = (Y_SHELF - 0.01) if S["rear"] else y_bh    # 2+2: on under the backrests to the boot wall
xl, xr_ = -XW[-1] - 0.03, XW[1] + 0.03          # tucked under the side walls: no gap at the floor line
rows = [(y1c, floor + 0.005), (yR + 0.28, floor + 0.005), (yR + 0.46, floor + 0.075)]
grid = [[bm.verts.new((x, y, z)) for x in (xl, xr_)] for (y, z) in rows]
for i in range(len(rows) - 1):
    bm.faces.new((grid[i][0], grid[i][1], grid[i + 1][1], grid[i + 1][0]))
for f in bm.faces:
    if f.normal.z < 0: f.normal_flip()
finish("RL_Carpet", bm, "carpet")

# ------------------------------------------------------------------ side walls: door cards and the panels behind them
for side in (-1, 1):
    w = SIDE[side]; x_in = w["x_in"]; belt = w["belt"]
    ny = Vector((-side, 0, 0)); rr = Vector((0, -side, 0)); up = Vector((0, 0, 1))
    zb = floor - 0.01
    y0d, y1d = w["y_b"], yR + 0.05                # the front door: B-pillar to the dash
    if y1d - y0d >= 0.3:
        panel(f"RL_DoorCard{side}", Vector((side * x_in, (y0d + y1d) / 2, (zb + belt) / 2)), rr, up, ny, y1d - y0d, belt - zb, 0.03, "door", bevel=0.01)
        # armrest with the window switches, the pull handle, a speaker low down
        az = E.z - 0.60
        ay = min(max(E.y + 0.15, y0d + 0.22), y1d - 0.22)
        box(f"RL_Armrest{side}", Vector((side * (x_in - 0.05), ay, az)), (0.08, 0.42, 0.05), "seat", bevel=0.015)
        for k in range(2):
            box(f"RL_WinSwitch{side}{k}", Vector((side * (x_in - 0.05), ay + 0.13 + k * 0.035, az + 0.03)), (0.02, 0.022, 0.012), "knob", bevel=0.0)
        box(f"RL_PullHandle{side}", Vector((side * (x_in - 0.035), min(ay + 0.27, y1d - 0.08), az + 0.12)), (0.025, 0.12, 0.03), "chrome", bevel=0.008)
        spk = Vector((side * (x_in - 0.032), y1d - 0.2, zb + 0.15))
        ring(f"RL_SpeakerRing{side}", spk, rr, up, ny, 0.075, 0.065, 0.008, "dash", segs=12)
        disc(f"RL_Speaker{side}", spk + ny * 0.002, rr, up, ny, 0.068, "vent", segs=12)
        box(f"RL_DoorShut{side}", Vector((side * (x_in - 0.003), y0d, (zb + belt) / 2)), (0.006, 0.008, belt - zb - 0.03), "dash", bevel=0.0)
    # behind the door: beside the rear seat / cargo area (2+2, hatch) or up to the bulkhead
    if y0d - Y_END > 0.05:
        panel(f"RL_RearSide{side}", Vector((side * x_in, (Y_END + y0d) / 2, (zb + belt) / 2)), rr, up, ny, y0d - Y_END, belt - zb, 0.03, "door", bevel=0.01)
        if S["rear"]:   # the rear passenger's armrest
            box(f"RL_RearArmrest{side}", Vector((side * (x_in - 0.045), RY - 0.05, RZ + 0.22)), (0.07, 0.40, 0.05), "seat", bevel=0.015)

# ------------------------------------------------------------------ the rear of the cabin
if REAR in ("bulkhead", "hatch"):
    # the wall behind the seats: carpeted below, trimmed panel above leaning back, a ledge on top (bulkhead: the
    # rear window above it; hatch: the cargo cover behind it)
    z_top = Z_PS if REAR == "hatch" else E.z - 0.20
    z_mid = floor + (z_top - floor) * 0.55
    wdt = XW[-1] + XW[1]
    # both reach 1.5 cm into the side walls: their bevelled ends left a see-through slit in the corners
    box("RL_BulkheadLow", Vector((XC, y_bh, (floor + z_mid) / 2)), (wdt + 0.03, 0.04, z_mid - floor), "carpet", bevel=0.006)
    Rb = Matrix.Rotation(math.radians(6), 3, "X")
    box("RL_BulkheadUp", Vector((XC, y_bh - 0.01, (z_mid + z_top) / 2)), (wdt + 0.03, 0.035, z_top - z_mid + 0.01), "door", rot=Rb, bevel=0.008)
    if REAR == "bulkhead":
        box("RL_BulkheadLedge", Vector((XC, y_bh - 0.03, z_top + 0.012)), (wdt - 0.004, 0.13, 0.024), "dash", bevel=0.008)
    else:
        box("RL_CargoCover", Vector((XC, (y_bh + y_rw) / 2, Z_PS - 0.01)), (wdt - 0.004, y_bh - y_rw, 0.02), "carpet", bevel=0.004)
        box("RL_CargoLip", Vector((XC, y_bh + 0.005, Z_PS + 0.004)), (wdt - 0.004, 0.05, 0.03), "dash", bevel=0.008)
if REAR == "2+2":
    # parcel shelf from the rear backrest to the rear window, with two speakers in it
    y_s0 = Y_SHELF if S["rear"] else y_bh
    box("RL_ParcelShelf", Vector((XC, (y_s0 + y_rw) / 2, Z_PS - 0.0125)), (XW[-1] + XW[1] - 0.004, y_s0 - y_rw, 0.025), "carpet", bevel=0.005)
    for s in (-1, 1):
        spk = Vector((XC + s * (XW[s] - 0.20) * 1.0, (y_s0 + y_rw) / 2, Z_PS + 0.001))
        disc(f"RL_ShelfSpeaker{s}", spk, Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1)), 0.06, "vent", segs=10)

# ------------------------------------------------------------------ footwell: pedals, dead pedal, kick panels
y_pd, z_pd = yR + 0.30, floor + 0.13
def under_dash_y(z):
    """The dash's lower surface (knee line down to the firewall foot) at height z, from dash_profile's points."""
    za, ya = floor + 0.42, yR + 0.17; zb_, yb = floor + 0.04, yR + 0.45
    return ya + (yb - ya) * (za - z) / (za - zb_)
pad_tag = "chrome" if DIAL["style"] in ("italian", "digital", "jdm", "german") else "knob"
pedals = [(-0.13, 0.055, 0.075), (-0.01, 0.075, 0.07), (0.13, 0.045, 0.11)] if TRIM["pedals"] == 3 else \
         [(-0.05, 0.11, 0.07), (0.13, 0.045, 0.11)]
Rp = Matrix.Rotation(math.radians(-30), 3, "X")   # pads lean back towards the driver's feet
for k, (dx, pw, ph) in enumerate(pedals):
    pc = Vector((P.x + dx, y_pd, z_pd))
    box(f"RL_Pedal{k}", pc, (pw, 0.014, ph), pad_tag, rot=Rp, bevel=0.0)
    z_arm = floor + 0.36
    cylinder(f"RL_PedalArm{k}", pc + Rp @ Vector((0, 0.01, ph * 0.3)), Vector((P.x + dx, under_dash_y(z_arm) + 0.03, z_arm)), 0.008, "dash", segs=6, smooth=False)
# the dead pedal: a foot rest beside the clutch, outboard, clear of the kick panel
dpx = P.x + DS * 0.25
dpx = DS * min(abs(dpx), XW[DS] - 0.06)
box("RL_DeadPedal", Vector((dpx, y_pd + 0.03, floor + 0.10)), (0.08, 0.014, 0.19), "knob", rot=Matrix.Rotation(math.radians(-40), 3, "X"), bevel=0.0)
# kick panels: the footwell's side walls under the dash, from the door card forward to the firewall
for side in (-1, 1):
    xk = side * (XW[side] - 0.001)
    outline = [(yR + 0.05, floor - 0.01), (ws_y + 0.2, floor - 0.01), (ws_y + 0.2, floor + 0.06), (yR + 0.45, floor + 0.06),
               (yR + 0.17, floor + 0.44), (yR + 0.05, zT - 0.10)]
    pts = [Vector((xk, y, z)) for (y, z) in outline]
    if side < 0: pts.reverse()
    finish(f"RL_KickPanel{side}", shapes.polygon_slab(pts, Vector((-side, 0, 0)), 0.03), "door")

# ------------------------------------------------------------------ cabin trim (group Trim): built where the tuned roof,
# pillars and doors show, so it needs no part offset of its own (Part.Trim starts at identity)
TG = "Trim"

def face_eye(bm):
    """Single-sided sheets: every face towards the driver's eye (the game culls back faces)."""
    for f in bm.faces:
        f.normal_update()
        if f.normal.dot(E - f.calc_center_median()) < 0: f.normal_flip()

# --- headliner: a thin curved sheet under the roof, rails dropping at the sides, a rounded nose down to the header
# and a rolled rear edge (two-seaters: it ends at the bulkhead)
TS = [-1.0, -0.96, -0.86, -0.68, -0.4, 0.0, 0.4, 0.68, 0.86, 0.96, 1.0]
def rail_drop(t): return -0.055 * max(0.0, (abs(t) - 0.5) / 0.5) ** 2
fy_rear = r_lo.y if REAR != "bulkhead" else min(max(file_y(y_bh), r_lo.y), r_hi.y - 0.25)
L = r_hi.y - fy_rear
ext = max(0.0, hdr_front - r_hi.y)
nose = (z_hdr - 0.004) - (zr - 0.012)          # how far the nose drops to meet the header line
ROWS = [(fy_rear + 0.012, -0.05), (fy_rear, -0.022), (fy_rear + 0.05, 0.0), (fy_rear + L * 0.35, 0.0),
        (fy_rear + L * 0.7, 0.0), (r_hi.y, 0.0), (r_hi.y + ext * 0.6 + 0.01, nose * 0.35), (r_hi.y + ext + 0.02, nose)]
HL = []   # rows (rear to front) of points (left to right)
for (yf, dz) in ROWS:
    HL.append([roof_pt((t + 1) / 2, yf, -0.012 + dz + rail_drop(t) * (1.0 if dz > -0.03 else 0.6)) for t in TS])
bm = bmesh.new()
gv = [[bm.verts.new(p) for p in row] for row in HL]
for i in range(len(HL) - 1):
    for j in range(len(TS) - 1):
        bm.faces.new((gv[i][j], gv[i][j + 1], gv[i + 1][j + 1], gv[i + 1][j]))
face_eye(bm)
hl_bvh = BVHTree.FromBMesh(bm)
finish("TR_Headliner", bm, "headliner", smooth=True, group=TG)

def liner_z(x, y):
    loc, _n, _i, _d = hl_bvh.ray_cast(Vector((x, y, E.z - 0.3)), Vector((0, 0, 1)), 2.0)
    return loc.z if loc is not None else zr - 0.012

def liner_edge(side, y):
    """The headliner's side edge at world y (clamped to its length)."""
    col = [row[0 if side < 0 else -1] for row in HL[2:6]]    # the flat part's edge, rear to front
    if y <= col[0].y: return col[0].copy()
    if y >= col[-1].y: return col[-1].copy()
    for a, b in zip(col, col[1:]):
        if a.y <= y <= b.y:
            t = (y - a.y) / max(1e-6, b.y - a.y); return a.lerp(b, t)
    return col[-1].copy()

front_corner = {-1: HL[-2][0], 1: HL[-2][-1]}           # where the A-pillars meet the headliner
rear_corner = {-1: HL[1][0], 1: HL[1][-1]}

# --- headliner side valances: a strip hanging from each side edge, a few mm outboard of it and overlapping it, down
# past the top of the window frame (framed doors) / the B and C pillars' tops, so no slit to the outside opens between
# the headliner and them when the head turns to the side
VAL_H = 0.012 if TRIM["frameless"] else 0.032
for side in (-1, 1):
    col = [row[0 if side < 0 else -1] for row in HL[1:-1]]          # rear corner .. front corner
    top = [q + Vector((-side * 0.006, 0, 0.004)) for q in col]
    bot = [q + Vector((side * 0.003, 0, -VAL_H)) for q in col]
    bm = bmesh.new()
    vt = [bm.verts.new(q) for q in top]; vb = [bm.verts.new(q) for q in bot]
    for i in range(len(col) - 1): bm.faces.new((vt[i], vt[i + 1], vb[i + 1], vb[i]))
    face_eye(bm)
    finish(f"TR_HeadlinerValance{side}", bm, "headliner", group=TG)

# --- A-pillar trims: tapered, slightly bowed covers from the dash corner up to the headliner
for side in (-1, 1):
    pv = tuned_group("Pillars", "APillarL" if side < 0 else "APillarR")
    if not pv: continue
    zs = sorted(pv, key=lambda v: v.z); k = max(1, len(pv) // 4)
    foot = sum(zs[:k], Vector()) / k; top = sum(zs[-k:], Vector()) / k
    d = (top - foot).normalized()
    start = foot - d * 0.05
    end = front_corner[side] + Vector((side * 0.012, 0.0, 0.004))
    path, inw = [], []
    for i in range(6):
        t = i / 5
        q = start.lerp(end, t)
        din = E - q; din = (din - d * din.dot(d)).normalized()   # towards the eye, across the pillar
        path.append(q + din * (0.012 * math.sin(math.pi * t))); inw.append(din)
    prof = shapes.rounded_profile(1.0, 1.0, 0.85, 0.0, segs=2)
    scales = [(0.095 - 0.03 * i / 5, 0.045 - 0.01 * i / 5) for i in range(6)]
    tang = [(path[min(i + 1, 5)] - path[max(i - 1, 0)]).normalized() for i in range(6)]
    bm = shapes.sweep(path, prof, lambda i: inw[i].cross(tang[i]), scales=scales)
    finish(f"TR_APillar{side}", bm, "headliner", smooth=True, group=TG)

# --- door-top caps: a padded roll along the belt from the A-pillar's foot back to the end of the side wall, over the
# fitted door's top edge; a thin dark seal on its outer edge (the window channel)
for side in (-1, 1):
    w = SIDE[side]
    x_in, x_out = w["x_in"], w["x_out"]
    ys = [Y_END, y_cap_front[side]]             # the cap follows the door top's straight line
    if side < 0: ys.reverse()                    # path direction = side * y, so the profile's v axis is up
    rings, seal = [], []
    for y in ys:
        zt = cap_top(side, y); H = zt - (w["belt"] - 0.03)
        Wd = (x_out + 0.004) - (x_in - 0.015)
        prof = shapes.rounded_profile(Wd, H, min(0.022, H * 0.45), 0.0, segs=3)
        xm = side * ((x_out + 0.004) + (x_in - 0.015)) / 2
        rings.append([Vector((xm - side * u, y, zt - H + v)) for (u, v) in prof])
        seal.append(Vector((side * (x_out - 0.004), y, zt - 0.002)))
    finish(f"TR_DoorCap{side}", shapes.loft(rings), "door", smooth=False, group=TG)
    finish(f"TR_WindowSeal{side}", shapes.sweep(seal, shapes.round_profile(0.007, 4), lambda i: Vector((0, 0, 1))), "dash", group=TG)

# --- B-pillars (2+2, hatch) or sail panels (bulkhead: door to bulkhead), C-pillars round the rear quarter windows
def side_sheet(name, bottom, top, tag="headliner"):
    """A 3 cm thick panel between two point rows (belt and roof rail), its visible face towards the cabin."""
    side = 1 if bottom[0].x > 0 else -1
    bm = shapes.sheet_solid([bottom, top], 0.03, lambda p: Vector((side, 0, 0)))
    finish(name, bm, tag, group=TG)

for side in (-1, 1):
    w = SIDE[side]
    xo = side * w["x_out"]
    y_b = w["y_b"]
    if REAR == "bulkhead" and y_b - y_bh > 0.08:
        ys = [y_bh + (y_b - y_bh) * i / 2 for i in range(3)]
        side_sheet(f"TR_Sail{side}", [Vector((xo, y, cap_top(side, y) - 0.04)) for y in ys],
                   [liner_edge(side, y) + Vector((side * 0.01, 0, -0.006)) for y in ys])
    else:
        bw = TRIM["b_w"]
        yc = y_b - bw / 2
        # from inside the cap (its foot hidden in it) up to the rail, the window line as its back
        a = Vector((xo + side * 0.004, yc, cap_top(side, yc) - 0.03)); b = liner_edge(side, yc) + Vector((side * 0.004, 0, -0.004))
        path = [a, a.lerp(b, 0.5) + Vector((-side * 0.004, 0, 0)), b]
        tang = (b - a).normalized()
        prof = shapes.rounded_profile(1.0, 1.0, 0.6, 0.0, segs=2)
        bm = shapes.sweep(path, prof, lambda i: Vector((-side, 0, 0)).cross(tang), scales=[(bw, 0.07), (bw * 0.9, 0.055), (bw * 0.8, 0.04)])
        finish(f"TR_BPillar{side}", bm, "headliner", smooth=True, group=TG)
    if REAR != "bulkhead":
        bw = TRIM["b_w"]
        y_cf = min(max(y_b - bw - TRIM["quarter"], Y0 + 0.14), y_b - bw + 0.01)   # C-pillar's front edge (roof)
        y_cfb = max(y_cf - 0.05, y_rw + 0.12)
        if TRIM["quarter"] < 0.05:   # no quarter window: the foot reaches forward under the B-pillar (no sliver at the cap)
            y_cfb = y_cf + 0.03
        yrr = rear_corner[side].y
        bot = [Vector((xo, y_cfb, cap_top(side, y_cfb) - 0.04)), Vector((xo, (y_cfb + y_rw) / 2, cap_top(side, (y_cfb + y_rw) / 2) - 0.04)),
               Vector((xo, y_rw, Z_PS - 0.02))]
        top = [liner_edge(side, y_cf) + Vector((side * 0.01, 0, -0.006)), liner_edge(side, (y_cf + yrr) / 2) + Vector((side * 0.01, 0, -0.006)),
               rear_corner[side] + Vector((side * 0.01, 0.0, -0.01))]
        side_sheet(f"TR_CPillar{side}", bot, top)

# --- window frames (framed doors): a thin dark frame up the B-pillar's front edge and along the roof rail
if not TRIM["frameless"]:
    for side in (-1, 1):
        w = SIDE[side]; y_b = w["y_b"] + 0.005
        a = Vector((side * (w["x_out"] - 0.01), y_b, cap_top(side, y_b)))
        path = [a, liner_edge(side, y_b) + Vector((-side * 0.01, 0, -0.012))]
        fc = front_corner[side] + Vector((-side * 0.012, -0.03, -0.014))
        for t in (0.35, 0.7):
            path.append(liner_edge(side, y_b + (fc.y - y_b) * t) + Vector((-side * 0.012, 0, -0.014)))
        path.append(fc)
        finish(f"TR_WindowFrame{side}", shapes.sweep(path, shapes.round_profile(0.011, 6), lambda i: Vector((side, 0, 0))), "dash", smooth=True, group=TG)

# --- rear window surround: a dark rubber frame round the rear glass (2+2, hatch) or the bulkhead window
if REAR == "bulkhead":
    yb_ = y_bh - 0.02
    xl_, xr2 = -(XW[-1] + 0.02), XW[1] + 0.02
    zt_ = max(liner_z(XC, y_bh + 0.06) - 0.05, E.z - 0.10)
    loop = [Vector((xl_, yb_, zt_)), Vector((xr2, yb_, zt_)), Vector((xr2, yb_, E.z - 0.17)), Vector((xl_, yb_, E.z - 0.17)), Vector((xl_, yb_, zt_))]
    # the panel between the headliner's end and the rear window (the bulkhead's header)
    box("TR_RearHeader", Vector((XC, y_bh - 0.005, (zt_ + liner_z(XC, y_bh + 0.06)) / 2 + 0.01)), (xr2 - xl_ + 0.04, 0.03, liner_z(XC, y_bh + 0.06) - zt_ + 0.04), "headliner", bevel=0.008, group=TG)
    for side in (-1, 1):   # the window's sides: from the bulkhead's edge out to the sail, ledge to headliner
        x0j, x1j = XW[side] - 0.01, SIDE[side]["x_out"] + 0.03
        zj0 = min(E.z - 0.21, cap_top(side, y_bh) - 0.03)   # the foot sits down in the door cap: no gap above it
        box(f"TR_RearJamb{side}", Vector((side * (x0j + x1j) / 2, y_bh - 0.005, (zj0 + zt_ + 0.05) / 2)), (x1j - x0j, 0.03, zt_ + 0.05 - zj0), "headliner", bevel=0.008, group=TG)
else:
    cl, cr = rear_corner[-1] + Vector((0.03, -0.005, -0.012)), rear_corner[1] + Vector((-0.03, -0.005, -0.012))
    bl, br = Vector((-(XW[-1] + 0.01), y_rw + 0.02, Z_PS + 0.012)), Vector((XW[1] + 0.01, y_rw + 0.02, Z_PS + 0.012))
    loop = [cl, cr, br, bl, cl]
finish("TR_RearWindowFrame", shapes.sweep(loop, shapes.round_profile(0.014, 6), lambda i: Vector((0, -1, 0))), "dash", smooth=True, group=TG)

# --- sun visors (rounded, 9 mm thick, a hinge rod and a clip) where the tuned ones hung
for vn in ("Visor-1", "Visor1"):
    vv = tuned_group("Roof", vn)
    if not vv: continue
    x0, x1 = min(v.x for v in vv), max(v.x for v in vv); y0, y1 = min(v.y for v in vv), max(v.y for v in vv)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    wv, dv_ = min(x1 - x0, 0.48), min(y1 - y0, 0.19)
    zt = min(liner_z(cx, cy), liner_z(cx, cy + dv_ / 2)) - 0.012
    prof = shapes.rounded_profile(wv, dv_, 0.03, 0.03, segs=3)
    outline = [Vector((cx + u, cy - dv_ / 2 + v, zt - 0.009)) for (u, v) in prof]
    finish(f"TR_{vn}", shapes.polygon_slab(outline, Vector((0, 0, -1)), 0.009), "door", smooth=False, group=TG)
    outer = 1 if abs(x1) > abs(x0) else -1      # the hinge is at the outer end (near the A-pillar)
    xo_, xi_ = (x1, x0) if outer > 0 else (x0, x1)
    ry_ = cy + dv_ / 2 - 0.012
    cylinder(f"TR_{vn}_Rod", Vector((xi_ + outer * 0.02, ry_, zt - 0.004)), Vector((xo_ + outer * 0.03, ry_, zt - 0.004)), 0.0045, "chrome", segs=6, group=TG)
    box(f"TR_{vn}_Hinge", Vector((xo_ + outer * 0.035, ry_, zt + 0.002)), (0.03, 0.03, 0.016), "dash", bevel=0.0, group=TG)
    box(f"TR_{vn}_Clip", Vector((xi_ - outer * 0.01, ry_, zt + 0.002)), (0.02, 0.025, 0.014), "dash", bevel=0.0, group=TG)

# --- grab handles over the doors and the dome light
for side in (-1, 1):
    yg = min(max(E.y + 0.08, HL[2][0].y + 0.15), HL[5][0].y - 0.15)
    if TRIM["frameless"]:
        # no frame to hang over: mounted on the headliner inboard of the edge, folded up against it, so no leg reaches
        # below the valance into the window opening
        xg = liner_edge(side, yg).x - side * 0.05
        base = Vector((xg, yg, liner_z(xg, yg) - 0.002))
        dn = Vector((-side * 1.0, 0, -0.30)).normalized(); fw = Vector((0, 1, 0))
    else:
        base = liner_edge(side, yg) + Vector((-side * 0.035, 0, -0.004))
        dn = Vector((-side * 0.45, 0, -1)).normalized(); fw = Vector((0, 1, 0))
    path = [base + fw * a + dn * b for (a, b) in ((-0.11, -0.004), (-0.10, 0.03), (-0.075, 0.048), (0.075, 0.048), (0.10, 0.03), (0.11, -0.004))]
    finish(f"TR_GrabHandle{side}", shapes.sweep(path, shapes.round_profile(0.0095, 6), lambda i: Vector((side, 0, 0))), "dash", smooth=True, group=TG)
yd = min(max(E.y - (0.18 if REAR != "bulkhead" else 0.10), HL[2][0].y + 0.12), HL[5][0].y - 0.15)
zd = liner_z(XC, yd)
finish("TR_DomeLight", shapes.polygon_slab([Vector((XC + u, yd + v, zd - 0.014)) for (u, v) in shapes.rounded_profile(0.17, 0.09, 0.025, 0.025, 2)],
       Vector((0, 0, -1)), 0.016), "dash", group=TG)
finish("TR_DomeLens", shapes.polygon_slab([Vector((XC + u, yd + 0.015 + v, zd - 0.019)) for (u, v) in shapes.rounded_profile(0.12, 0.06, 0.02, 0.02, 2)],
       Vector((0, 0, -1)), 0.006), "chrome", group=TG)

# ------------------------------------------------------------------ rear-view mirror: a rounded housing round the kept
# glass and a stem up to the headliner, in the RearMirror group's own (file) space so its Part offset still applies
RM = dict(parts=[p for p in old["parts"] if p["group"] == "RearMirror"], new=[])
glass_v = verts_of(group="RearMirror", name_has="RearMirrorGlass")
if glass_v:
    TM = group_T("RearMirror"); TMi = TM.inverted()
    g_lo = Vector((min(v.x for v in glass_v), min(v.y for v in glass_v), min(v.z for v in glass_v)))
    g_hi = Vector((max(v.x for v in glass_v), max(v.y for v in glass_v), max(v.z for v in glass_v)))
    gw, gh = g_hi.x - g_lo.x, g_hi.z - g_lo.z
    gc = Vector(((g_lo.x + g_hi.x) / 2, g_lo.y, (g_lo.z + g_hi.z) / 2))
    hp = shapes.rounded_profile(gw + 0.018, gh + 0.018, min(0.02, (gh + 0.018) * 0.48), min(0.02, (gh + 0.018) * 0.48), segs=3)   # r 2 cm: the glass corners stay inside
    rings = []
    for (dy, sc) in ((0.003, 1.0), (0.02, 1.0), (0.042, 0.82)):
        rings.append([Vector((gc.x + u * sc, gc.y + dy, gc.z - (gh + 0.018) / 2 * sc + v * sc)) for (u, v) in hp])
    RM["new"].append(("RL_MirrorHousing", shapes.loft(rings), "interior_dark", True))
    stem_v = verts_of(group="RearMirror", name_has="RearMirrorStem")
    sx_ = sum(v.x for v in stem_v) / len(stem_v) if stem_v else gc.x
    a = Vector((sx_, gc.y + 0.025, g_hi.z + 0.004))
    aw = TM @ a
    top_w = Vector((aw.x, aw.y + 0.03, liner_z(aw.x, aw.y + 0.03) + 0.004))   # the headliner where the stem meets it
    b = TMi @ top_w
    if (b - a).length < 0.01: b = a + Vector((0, 0.02, 0.03))
    bm = bmesh.new(); bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=8, radius1=0.008, radius2=0.008, depth=(b - a).length)
    ax_ = (b - a).normalized()
    bmesh.ops.rotate(bm, verts=bm.verts, cent=Vector(), matrix=Vector((0, 0, 1)).rotation_difference(ax_).to_matrix())
    bmesh.ops.translate(bm, vec=(a + b) / 2, verts=bm.verts)
    RM["new"].append(("RL_MirrorStem", bm, "metal", True))
    # the stem's foot on the headliner
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0); bmesh.ops.scale(bm, vec=Vector((0.05, 0.04, 0.014)), verts=bm.verts)
    bmesh.ops.translate(bm, vec=TMi @ (top_w + Vector((0, 0, -0.004))), verts=bm.verts)
    RM["new"].append(("RL_MirrorMount", bm, "interior_dark", False))

# ------------------------------------------------------------------ steering wheel (pivot space: x right, y forward, z up)
W = S["wheel"]
rim_tag = "wood" if W.get("wood") else "wheel"
bm = bmesh.new()
segs, tsegs = 28, 7
grid = []
for i in range(segs):
    a = 2 * math.pi * i / segs
    c = Vector((math.cos(a) * W["r"], 0, math.sin(a) * W["r"]))
    rad = Vector((math.cos(a), 0, math.sin(a)))
    row = []
    for j in range(tsegs):
        b = 2 * math.pi * j / tsegs
        row.append(bm.verts.new(c + (rad * math.cos(b) + Vector((0, 1, 0)) * math.sin(b)) * W["t"]))
    grid.append(row)
for i in range(segs):
    for j in range(tsegs):
        bm.faces.new((grid[i][j], grid[(i + 1) % segs][j], grid[(i + 1) % segs][(j + 1) % tsegs], grid[i][(j + 1) % tsegs]))
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
finish("RL_WheelRim", bm, rim_tag, smooth=True, group="SteeringWheel", pivot=True)
spoke_tag = "chrome" if W.get("alu_spokes") else "wheel"
for k, a in enumerate(W["spokes"]):
    ar = math.radians(a)
    d = Vector((math.cos(ar), 0, math.sin(ar)))
    mid = d * ((W["r"] + W["hub_r"]) / 2) + Vector((0, 0.012, 0))
    rot = Matrix.Rotation(-ar, 3, "Y")
    box(f"RL_Spoke{k}", mid, (W["r"] - W["hub_r"] + 0.01, 0.012, W["spoke_w"]), spoke_tag, rot=rot, bevel=0.004, group="SteeringWheel", pivot=True)
cylinder("RL_Hub", Vector((0, 0.0, 0)), Vector((0, 0.05, 0)), W["hub_r"], "wheel", segs=24, group="SteeringWheel", pivot=True)
cylinder("RL_HornPad", Vector((0, -0.008, 0)), Vector((0, 0.0, 0)), W["hub_r"] * 0.85, "bezel" if W.get("alu_spokes") else "wheel", segs=24, group="SteeringWheel", pivot=True)
cylinder("RL_Column", Vector((0, 0.05, 0)), Vector((0, 0.30, 0)), 0.032, "dash", segs=16, group="SteeringWheel", pivot=True)
box("RL_ColumnShroud", Vector((0, 0.17, -0.02)), (0.1, 0.2, 0.08), "dash", bevel=0.02, group="SteeringWheel", pivot=True)

# ------------------------------------------------------------------ mirror sightlines: nothing of the modelled cabin may
# stand between the tuned eye and a mirror's glass. HeadLook only turns the view about the eye, so what hides a glass
# hides it at every head angle. Each cabin piece (Interior / Trim, not the moving pivot parts) that reaches into the
# frustum from the eye to a mirror (its outline seen from the eye) gets that frustum cut out of it:
# closed solids with an exact boolean (they stay closed, so the outline pass shows no inside faces through the cut),
# open sheets (the headliner) by splitting along the frustum's sides and dropping what is inside. The frustum is the
# whole mirror's outline (side mirror: glass and bezel; rear-view mirror: glass and its new housing), so from the eye
# the mirror simply shows in front of the trim: the cut faces are edge-on, and the cut is a few mm inside the bezel /
# housing outline, so no sliver of the outside opens round it while all of the glass shows. Mirrors are
# tested where the game shows them (their tuned Part offsets); the cut walls follow rays from the tuned eye, so moving
# Driver.Offset* a lot later can show them (rebuild then).
SIGHT_INSET = 0.004      # metres the cut stays inside the mirror's outline, at the mirror (bezel / housing rims are 9+ mm)
SIGHT_NEAR = 0.03        # the eye camera's near clip

def hull2(pts):
    """Convex hull (counter-clockwise) of 2D points."""
    pts = sorted(set(pts))
    if len(pts) < 3: return pts
    def cross2(o, p, q): return (p[0] - o[0]) * (q[1] - o[1]) - (p[1] - o[1]) * (q[0] - o[0])
    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross2(lower[-2], lower[-1], p) <= 0: lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross2(upper[-2], upper[-1], p) <= 0: upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]

def mirror_frustum(gv, glass, name):
    """The frustum from the eye to a mirror (gv = all its points, glass = its glass's, where the game shows them):
    dict(d, a, b, dist, near, far, hull) with hull its outline seen from the eye (convex, 2D in the a/b plane at `dist`
    along d): the whole outline SIGHT_INSET smaller, but never smaller than the glass's own outline (+1 mm)."""
    if not gv or not glass: return None
    c = sum(gv, Vector()) / len(gv)
    d = (c - E).normalized()
    a = d.cross(Vector((0, 0, 1)))
    a = a.normalized() if a.length > 1e-6 else Vector((1, 0, 0))
    b = a.cross(d).normalized()
    dist = (c - E).dot(d)
    def proj(vs):
        out = []
        for v in vs:
            t = (v - E).dot(d)
            if t <= 0.05: continue
            q = (v - E) * (dist / t)
            out.append((q.dot(a), q.dot(b)))
        return out
    def grown(hull, m):   # moved m outwards from the hull's centre (m < 0: inwards)
        cx = sum(p[0] for p in hull) / len(hull); cy = sum(p[1] for p in hull) / len(hull)
        out = []
        for p in hull:
            dx, dy = p[0] - cx, p[1] - cy; L = math.hypot(dx, dy)
            k = max(0.0, L + m) / L if L > 1e-9 else 0.0
            out.append((cx + dx * k, cy + dy * k))
        return out
    whole, gl = hull2(proj(gv)), hull2(proj(glass))
    if len(whole) < 3 or len(gl) < 3: return None
    hull = hull2(grown(whole, -SIGHT_INSET) + grown(gl, 0.001))
    far = max((v - E).dot(d) for v in gv) + 0.01
    return dict(d=d, a=a, b=b, dist=dist, near=SIGHT_NEAR, far=far, hull=hull, name=name)

def frustum_point(fr, x, y, t):
    return E + (fr["a"] * x + fr["b"] * y) * (t / fr["dist"]) + fr["d"] * t

def frustum_planes(fr):
    """Inward-facing side planes (point, normal) of the frustum, plus the near and far caps."""
    out = []
    h = fr["hull"]
    for i in range(len(h)):
        p0 = frustum_point(fr, *h[i], fr["dist"]); p1 = frustum_point(fr, *h[(i + 1) % len(h)], fr["dist"])
        n = (p0 - E).cross(p1 - E).normalized()
        if n.dot(fr["d"] * fr["dist"] + E - (p0 + p1) / 2) < 0: n = -n    # inward: towards the frustum's axis
        out.append((E, n))
    out.append((E + fr["d"] * fr["near"], fr["d"].copy()))
    out.append((E + fr["d"] * fr["far"], -fr["d"]))
    return out

def inside_frustum(planes, p, eps=0.0):
    return all((p - q).dot(n) > eps for (q, n) in planes)

def frustum_cutter(fr):
    bm = bmesh.new()
    rings = [[bm.verts.new(frustum_point(fr, x, y, t)) for (x, y) in fr["hull"]] for t in (fr["near"], fr["far"])]
    m = len(fr["hull"])
    for k in range(m):
        bm.faces.new((rings[0][k], rings[0][(k + 1) % m], rings[1][(k + 1) % m], rings[1][k]))
    bm.faces.new(rings[0]); bm.faces.new(list(reversed(rings[1])))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new("SightCutter"); bm.to_mesh(me); bm.free()
    for p in me.polygons: p.use_smooth = False
    ob = bpy.data.objects.new("SightCutter", me); bpy.context.scene.collection.objects.link(ob)
    return ob

def reaches_into(ob, planes, cutter_bvh):
    me = ob.data
    if any(inside_frustum(planes, v.co) for v in me.vertices): return True
    return bool(BVHTree.FromObject(ob, bpy.context.evaluated_depsgraph_get()).overlap(cutter_bvh))

def is_closed(me):
    use = {}
    for p in me.polygons:
        for ek in p.edge_keys: use[ek] = use.get(ek, 0) + 1
    return all(n == 2 for n in use.values())

def cut_closed(ob, cutter):
    mod = ob.modifiers.new("Sight", "BOOLEAN"); mod.operation = "DIFFERENCE"; mod.solver = "EXACT"; mod.object = cutter
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(dg))
    ob.modifiers.remove(mod)
    old_me = ob.data; ob.data = me; bpy.data.meshes.remove(old_me)
    # the cut walls lie on the cutter: flat, so the piece's smooth shading does not bend round the cut edge
    cb = BVHTree.FromObject(cutter, dg)
    for p in me.polygons:
        loc, _n, _i, dd = cb.find_nearest(p.center)
        if loc is not None and dd < 1e-4: p.use_smooth = False

def cut_sheet(ob, planes, t_back=None):
    """Split only the faces whose bounding sphere meets the frustum, so the rest of the sheet keeps its faces.
    t_back (rear-view mirror): also returns a bmesh, a closed recess behind the cut: the cut-out part pushed back along
    the rays from the eye to depth t_back (behind the whole mirror) and walls from the cut's edge back to it. The walls
    lie on the frustum's sides (edge-on from the tuned eye) and the back is behind the mirror, so from the tuned eye
    the recess is hidden; from an eye a little off it (Driver.Offset* changed, a lean), what shows round the mirror
    through the cut is headliner, not the sky."""
    bm = bmesh.new(); bm.from_mesh(ob.data)
    def near(f):
        c = f.calc_center_median(); r = max((v.co - c).length for v in f.verts) + 1e-4
        return all((c - q).dot(n) > -r for (q, n) in planes)
    for (q, n) in planes[:-2]:
        faces = [f for f in bm.faces if near(f)]
        if not faces: break
        edges = {e for f in faces for e in f.edges}; verts = {v for f in faces for v in f.verts}
        bmesh.ops.bisect_plane(bm, geom=list(verts) + list(edges) + faces, plane_co=q, plane_no=n, dist=1e-6)
    gone = [f for f in bm.faces if inside_frustum(planes, f.calc_center_median(), 1e-6)]
    plug = None
    if t_back and gone:
        ax = planes[-2][1]                                     # the frustum's axis (the near cap's normal)
        plug = bmesh.new(); front, back = {}, {}
        def pv(v):
            if v not in back:
                r = v.co - E; td = r.dot(ax)
                back[v] = plug.verts.new(E + r * max(1.0, t_back / td) if td > 1e-4 else v.co + ax * 0.02)
            return back[v]
        def fv(v):
            if v not in front: front[v] = plug.verts.new(v.co.copy())
            return front[v]
        cap = []
        gs = set(gone)
        for f in gone:
            try: cap.append(plug.faces.new([pv(v) for v in f.verts]))
            except ValueError: pass
        for f in gone:   # walls along the cut: edges between a removed and a kept face
            for e in f.edges:
                if not any(g not in gs for g in e.link_faces): continue
                a, b = e.verts
                plug.faces.new((fv(a), fv(b), pv(b), pv(a)))
                # its other side: own vertices (bmesh keeps one face per vertex set), so it shows from both sides
                plug.faces.new([plug.verts.new(q.co.copy()) for q in (fv(b), fv(a), pv(a), pv(b))])
        for pf in cap:   # the back faces the eye
            pf.normal_update()
            if pf.normal.dot(E - pf.calc_center_median()) < 0: pf.normal_flip()
    bmesh.ops.delete(bm, geom=gone, context="FACES")
    bm.to_mesh(ob.data); bm.free()
    return plug

SIGHT = []
for g in ("MirrorLeft", "MirrorRight"):
    if g not in DROP_GROUPS: SIGHT.append((tuned_group(g), tuned_group(g, "SideMirrorGlass"), g))
if glass_v:   # the rear-view glass and the housing built round it (RearMirror file space -> where the game shows it)
    rg = tuned_group("RearMirror", "RearMirrorGlass")
    SIGHT.append((rg + [TM @ v.co for (n_, bm_, _t, _s) in RM["new"] if n_ == "RL_MirrorHousing" for v in bm_.verts], rg, "RearMirror"))
for (gv, glass, g) in SIGHT:
    fr = mirror_frustum(gv, glass, g)
    if fr is None: continue
    planes = frustum_planes(fr)
    cutter = frustum_cutter(fr)
    cbvh = BVHTree.FromObject(cutter, bpy.context.evaluated_depsgraph_get())
    t_back = None
    if g == "RearMirror":   # the rear-view mirror sits against the headliner: a recess behind the cut (see cut_sheet)
        all_rm = rg + [TM @ v.co for (_n, bm_, _t, _s) in RM["new"] for v in bm_.verts]   # glass, housing, stem, mount
        t_back = max((v - E).dot(fr["d"]) for v in all_rm) + 0.015
    hit, plugs = [], []
    for (ob, tag, group, smooth, textured, pivot) in OBJS:
        if pivot is not False or group not in ("Interior", "Trim"): continue
        if not reaches_into(ob, planes, cbvh): continue
        n0 = len(ob.data.polygons)
        if is_closed(ob.data): cut_closed(ob, cutter)
        else:
            plug = cut_sheet(ob, planes, t_back)
            if plug is not None: plugs.append((ob.name, plug, tag, group))
        hit.append(f"{ob.name} ({n0}->{len(ob.data.polygons)} faces)")
    for (nm, plug, tag, group) in plugs:
        hit.append(f"recess behind {nm} ({len(plug.faces)} faces)")
        finish(f"{nm}_Recess", plug, tag, group=group)
    bpy.data.objects.remove(cutter, do_unlink=True)
    if hit: print(f"[{CAR}] sightline to {fr['name']}: cut {', '.join(hit)}")

# ------------------------------------------------------------------ export
def fmt(v): return " ".join(f"{c:.5f}" for c in v)

def tri_lines(ob, textured, pivot):
    me = ob.data
    me.calc_loop_triangles()
    uvl = me.uv_layers.active.data if (textured and me.uv_layers) else None
    out = []
    for lt in me.loop_triangles:
        pts = []
        for li in (lt.loops[0], lt.loops[2], lt.loops[1]):
            v = me.vertices[me.loops[li].vertex_index].co
            nn = me.corner_normals[li].vector.normalized()
            pu = (v.x, v.z, v.y); nu = (nn.x, nn.z, nn.y)
            s = fmt(pu) + " " + fmt(nu)
            if uvl is not None: s += f" {uvl[li].uv.x:.6f} {uvl[li].uv.y:.6f}"
            pts.append(s)
        out.append(("u " if uvl is not None else "f ") + " ".join(pts))
    return out

raw = open(OLD, encoding="utf-8").read().splitlines()
header, blocks, cur = [], [], None
for line in raw:
    if line.startswith("o "):
        cur = [line]; blocks.append(cur)
    elif cur is None:
        if not line.startswith(("mat ", "tex gauges ", "tex gauges_mph ")): header.append(line)
    else:
        cur.append(line)
def group_of(block):
    for l in block:
        if l.startswith("g "): return l[2:].strip()
    return "Misc"
def name_of(block): return block[0][2:].strip()

# parts the Trim group replaces (bare boxes of the auto-fit) and the rear-view mirror pieces rebuilt above
REPLACED = ("APillar", "RoofLiner", "Header", "Visor")
def replaced(block):
    n = name_of(block)
    if any(r in n for r in REPLACED): return True
    return glass_v and group_of(block) == "RearMirror" and (n.endswith("RearMirror") or n.endswith("RearMirrorStem"))
kept = [b for b in blocks if group_of(b) not in ("Interior", "SteeringWheel") and group_of(b) not in DROP_GROUPS and not replaced(b)]

# --- clip the body-fitted Shell / Doors where they poke into the modelled cabin: inside the cabin's air (inboard of
# the side walls; a corner more than 6 mm through a door card goes), the footwell (inboard of the kick panels), round
# the window sills above the door caps, outboard of the caps from the belt line up (door tops, rear quarters behind the
# B-pillar), and behind the cabin's rear end (only seen through the rear window). Tested where the game shows them (tuned).
CLIP = []   # (lo, hi, depth): a triangle goes when its centre is inside, or any corner deeper than `depth`
for side in (-1, 1):
    xi = XW[side] - 0.012
    xa, xb = (0.0, xi) if side > 0 else (-xi, 0.0)
    CLIP.append((Vector((xa, Y_END + 0.01, floor + 0.02)), Vector((xb, yR, zr + 0.2)), 0.006))          # cabin: nothing through the door card
    CLIP.append((Vector((xa, yR - 0.01, floor + 0.02)), Vector((xb, ws_y + 0.25, zT - 0.03)), 0.03))   # footwell
    w = SIDE[side]
    xs0, xs1 = sorted((side * (w["x_in"] - 0.15), side * (w["x_out"] + 0.35)))
    ztop = min(cap_top(side, Y_END), cap_top(side, y_cap_front[side])) + 0.012
    y_fw = max(y_cap_front[side] + 0.15, ws_y + 0.35)
    CLIP.append((Vector((xs0, Y_END - 0.02, ztop)), Vector((xs1, y_fw, zr + 0.2)), 0.004))  # window sills: nothing sticks up
    xu0, xu1 = sorted((side * (w["x_in"] - 0.02), side * (w["x_out"] + 0.06)))
    CLIP.append((Vector((xu0, Y_END - 0.02, floor - 0.3)), Vector((xu1, y_fw, ztop - 0.025)), 0.03))  # under the cap
    # outboard of the cap, from the belt line up, along the doors and behind the B-pillar: the door tops' and rear
    # quarters' outer skins show there past the cap's outer edge as dark shards (the game's own body is drawn there)
    xo0, xo1 = sorted((side * (w["x_out"] - 0.01), side * (w["x_out"] + 0.35)))
    CLIP.append((Vector((xo0, Y_END - 0.02, w["belt"])), Vector((xo1, y_cap_front[side], zr + 0.2)), 0.004))  # belt line
    xd0, xd1 = sorted((side * (w["x_out"] + 0.01), side * (w["x_out"] + 0.35)))
    CLIP.append((Vector((xd0, Y_END - 0.02, floor - 0.3)), Vector((xd1, y_cap_front[side], w["belt"] + 0.01)), 0.004))  # outer skins below it
CLIP.append((Vector((-XW[-1], Y_END - 0.02, -9.0)), Vector((XW[1], ws_y + 0.3, floor - 0.005)), 0.03))     # under the floor
CLIP.append((Vector((-9.0, -99.0, -9.0)), Vector((9.0, Y_END - 0.03, 9.0)), 0.03))                     # behind the cabin

def depth_in(p, lo, hi):
    return min(p.x - lo.x, hi.x - p.x, p.y - lo.y, hi.y - p.y, p.z - lo.z, hi.z - p.z)

def clipped(pts):
    c = (pts[0] + pts[1] + pts[2]) / 3
    for lo, hi, dmax in CLIP:
        if depth_in(c, lo, hi) > 0: return True
        if max(depth_in(p, lo, hi) for p in pts) > dmax: return True
    return False

def tri_points(t, k):
    st = 8 if k == "u" else 6
    return [U(float(t[1 + i * st]), float(t[2 + i * st]), float(t[3 + i * st])) for i in range(3)]

CLIP_GROUPS = ("Shell", "Doors")
GT = {g: group_T(g) for g in set(group_of(b) for b in kept)}
removed = {}
for b in kept:
    g = group_of(b)
    if g not in CLIP_GROUPS: continue
    keep_lines = []
    for l in b:
        t = l.split()
        if t and t[0] in ("f", "u") and clipped([GT[g] @ p for p in tri_points(t, t[0])]):
            removed[g] = removed.get(g, 0) + 1; continue
        keep_lines.append(l)
    b[:] = keep_lines
for g, n in removed.items(): print(f"[{CAR}] clipped {n} {g} triangles that poked into the cabin")

# --- a group whose geometry changed keeps showing where it did: DriverCam turns / scales a group around its bbox
# centre, so every vertex of the group moves by d = (I - R S)(c_old - c_new) (zero for an unrotated, unscaled group)
def block_points(bl):
    out = []
    for l in bl:
        t = l.split()
        if t and t[0] in ("f", "u"): out += tri_points(t, t[0])
    return out
def bbox_c(pts):
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return (lo + hi) / 2
SHIFT = {}
new_rm = RM["new"]
for g in set(group_of(b) for b in kept):
    old_pts = verts_of(group=g)
    new_pts = [p for b in kept if group_of(b) == g for p in block_points(b)]
    if g == "RearMirror": new_pts += [v.co.copy() for (_n, bm_, _t, _s) in new_rm for v in bm_.verts]
    if not old_pts or not new_pts: continue
    c_old, c_new = bbox_c(old_pts), bbox_c(new_pts)
    if (c_old - c_new).length < 1e-6: continue
    gp, grot, gsc = part_tf(g)
    d = (c_old - c_new) - (grot * gsc) @ (c_old - c_new)
    if d.length > 1e-5:
        SHIFT[g] = d
        print(f"[{CAR}] group {g}: centre moved {(c_new - c_old).length * 100:.1f} cm, vertices shifted {d.length * 1000:.1f} mm to keep its tuned place")

def shifted_line(l, d):
    t = l.split(); st = 8 if t[0] == "u" else 6
    for i in range(3):
        b0 = 1 + i * st
        p = U(float(t[b0]), float(t[b0 + 1]), float(t[b0 + 2])) + d
        t[b0:b0 + 3] = [f"{c:.5f}" for c in dcm_io.b2u(p)]
    return " ".join(t)

lines = [f"# DriverCam cockpit for {CAR} with a modelled interior after the {S['real']} (built by build_interior.py, body coordinates, metres).",
         f"# The Shell, Doors, MirrorLeft and MirrorRight groups (clipped round the cabin) are the layout-1 auto-fit of the game's {CAR} body (autofit.py); everything else is modelled here."]
lines += [h for h in header if not h.startswith("#") and not h.startswith("layout")]
lines.append("layout 2")   # modelled interior, built at the tuned dash position (DriverCam migrates old Part.Interior once)
lines.append(f"tex gauges {CAR}_gauges.png")
lines.append(f"tex gauges_mph {CAR}_gauges_mph.png")   # the same faces in mph: DriverCam shows the game's unit
# the model's own colours: the new tags, and the old cockpit tags recoloured to match this cabin
mats = dict(PAL)
mats["interior_mid"] = PAL["door"]; mats["interior_dark"] = PAL["dash_top"]; mats["trim_dark"] = PAL["dash"]
mats["metal"] = PAL["chrome"]; mats["shell"] = mats.get("shell", PAL["dash"])
for tag, c in mats.items():
    if tag == "shell" and "tex shell" in "\n".join(header): continue
    lines.append(f"mat {tag} " + " ".join(f"{x:.4f}" for x in c))
for b in kept:
    d = SHIFT.get(group_of(b))
    if d is None: lines += b
    else: lines += [shifted_line(l, d) if l.split() and l.split()[0] in ("f", "u") else l for l in b]
# the rebuilt rear-view mirror pieces, in the RearMirror group's file space
for (n, bm_, tag, sm) in new_rm:
    d = SHIFT.get("RearMirror")
    if d is not None: bmesh.ops.translate(bm_, vec=d, verts=bm_.verts)
    ob = finish(n, bm_, tag, sm, group="RearMirror")
    OBJS.pop()   # written here, not with the cabin below (it lives in file space, not where the game shows it)
    lines += [f"o {n}", "g RearMirror", f"m {tag}"] + tri_lines(ob, False, False)

for (ob, tag, group, smooth, textured, pivot) in OBJS:
    if group == "SteeringWheel" or isinstance(pivot, dict): continue
    lines += [f"o {ob.name}", f"g {group}", f"m {tag}"] + tri_lines(ob, textured, pivot)
# gauge parts (needles, the digital readout): pivot, gauge lines, then each piece's tag and triangles in pivot space
gauge_parts = {}
for (ob, tag, group, smooth, textured, pivot) in OBJS:
    if isinstance(pivot, dict): gauge_parts.setdefault(pivot["part"], (pivot, group, []))[2].append((ob, tag, textured))
for name, (pv, group, pieces) in gauge_parts.items():
    lines += [f"o {name}", f"g {group}", f"p {fmt(dcm_io.b2u(pv['P']))} {fmt(dcm_io.b2u(pv['F']))} {fmt(dcm_io.b2u(pv['U']))}"] + pv["lines"]
    for (ob, tag, textured) in pieces:
        lines += [f"m {tag}"] + tri_lines(ob, textured, True)
# wheel: one part with a pivot, several material tags
lines += ["o SteeringWheel", "g SteeringWheel", f"p {fmt(Pp)} {fmt(Pf)} {fmt(Pu)}"]
for (ob, tag, group, smooth, textured, pivot) in OBJS:
    if group != "SteeringWheel": continue
    lines += [f"m {tag}"] + tri_lines(ob, textured, pivot)

os.makedirs(OUTDIR, exist_ok=True)
outp = os.path.join(OUTDIR, f"cockpit_{CAR}.dcm")
open(outp, "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
ntri = sum(1 for l in lines if l.startswith(("f ", "u ")))
per_g = {}
gcur = None
for l in lines:
    if l.startswith("g "): gcur = l[2:].strip()
    elif l.startswith(("f ", "u ")): per_g[gcur] = per_g.get(gcur, 0) + 1
print(f"[{CAR}] wrote {outp}: {ntri} triangles ({', '.join(f'{g} {n}' for g, n in per_g.items())})")

# ------------------------------------------------------------------ preview from the driver's eye: the written file,
# each group at its tuned place (layout 2: Interior and Trim at identity), back faces culled like the game
if PREVIEW:
    dcm_io.clear_scene()
    model = dcm_io.parse(outp)
    objs = dcm_io.to_blender(model, bpy.context.scene.collection, mats={k: v[:3] for k, v in mats.items()})
    by_g = {}
    for o, part in zip(objs, model["parts"]): by_g.setdefault(part["group"], []).append((o, part))
    for g, lst in by_g.items():
        pts = []
        for o, part in lst:
            if part["pivot"] is not None: pts.append(U(*part["pivot"][0]))
            else: pts += [v.co.copy() for v in o.data.vertices]
        if not pts or g in ("Interior", "Trim"): continue
        c = bbox_c(pts)
        gp, grot, gsc = part_tf(g)
        T = Matrix.Translation(c + U(*gp)) @ grot.to_4x4() @ Matrix.Scale(gsc, 4) @ Matrix.Translation(-c)
        for o, _p in lst: o.matrix_world = T
    dcm_io.camera_at_eye(tuple(eye_u), fov_deg=TUNE["View.Fov"], yaw=0, pitch=-TUNE["Driver.Pitch"], vertical=True)
    dcm_io.render(os.path.join(PREVIEW, f"{CAR}_front.png"))
    for (nm, yw, pt) in (("right", 70, -12), ("left", -75, -10), ("back", 160, -8), ("down", 15, -45), ("up", -40, 40)):
        dcm_io.camera_at_eye(tuple(eye_u), fov_deg=TUNE["View.Fov"], yaw=yw, pitch=pt, vertical=True)
        dcm_io.render(os.path.join(PREVIEW, f"{CAR}_{nm}.png"), 640, 360)
