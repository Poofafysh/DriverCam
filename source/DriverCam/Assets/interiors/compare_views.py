"""Before/after check for a cockpit change: renders two .dcm files from the driver's eye and compares the pictures.

blender -b --factory-startup --python compare_views.py -- <before.dcm> <after.dcm> <out dir> [views]

views: comma list of yaw angles in degrees (default 0,-70,70,180: ahead, left, right, behind; positive yaw = right).
Writes <out>/<name>_<before|after>_<yaw>.png and prints, per view, how many pixels differ (any channel off by more than
2/255) and the largest difference. Same camera, lights and colours (the model's own `mat` lines) for both files.
"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy
import dcm_io

argv = sys.argv[sys.argv.index("--") + 1:]
A, B, OUT = argv[0], argv[1], argv[2]
VIEWS = [float(x) for x in argv[3].split(",")] if len(argv) > 3 else [0.0, -70.0, 70.0, 180.0]
os.makedirs(OUT, exist_ok=True)
name = os.path.splitext(os.path.basename(A))[0]


def shoot(src, label):
    dcm_io.clear_scene()
    m = dcm_io.parse(src)
    dcm_io.to_blender(m, bpy.context.scene.collection, mats=m["mats"])
    files = []
    for yaw in VIEWS:
        dcm_io.camera_at_eye(m["eye"], yaw=yaw, pitch=-6.0)
        f = os.path.join(OUT, f"{name}_{label}_{int(yaw)}.png")
        dcm_io.render(f, 640, 360)
        files.append(f)
    return files


fa = shoot(A, "before")
fb = shoot(B, "after")
worst = 0
for yaw, a, b in zip(VIEWS, fa, fb):
    ia = bpy.data.images.load(a); ib = bpy.data.images.load(b)
    pa = list(ia.pixels); pb = list(ib.pixels)
    diff = 0; big = 0.0
    for i in range(0, len(pa), 4):
        d = max(abs(pa[i] - pb[i]), abs(pa[i + 1] - pb[i + 1]), abs(pa[i + 2] - pb[i + 2]))
        if d > 2 / 255: diff += 1
        if d > big: big = d
    worst = max(worst, diff)
    print(f"VIEW {name} yaw {int(yaw):4d}: {diff} of {len(pa) // 4} pixels differ, largest difference {big * 255:.0f}/255")
print(f"RESULT {name}: {'IDENTICAL' if worst == 0 else 'DIFFERENT'} (worst view {worst} pixels)")
