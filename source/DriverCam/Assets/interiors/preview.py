"""blender -b --factory-startup --python preview.py -- <in.dcm> <out.png> [yaw] [pitch]"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy
import dcm_io

argv = sys.argv[sys.argv.index("--") + 1:]
src, out = argv[0], argv[1]
yaw = float(argv[2]) if len(argv) > 2 else 0.0
pitch = float(argv[3]) if len(argv) > 3 else -6.0
dcm_io.clear_scene()
m = dcm_io.parse(src)
dcm_io.to_blender(m, bpy.context.scene.collection, mats=m["mats"])
dcm_io.camera_at_eye(m["eye"], yaw=yaw, pitch=pitch)
dcm_io.render(out)
print("rendered", out)
