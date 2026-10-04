# Driver character (Driver plugin, `rogue.driver`)

Original, fully scripted character: no game meshes, no sample or third-party content. Everything here is rebuilt
by `build_driver.py`; the `.blend` is a git-ignored working file.

## Build

```
blender -b --factory-startup --python build_driver.py -- [--out DIR] [--no-fbx]
blender -b DIR/driver.blend --python preview.py -- <previewdir> [standing seated hands eye lod1 detail handortho]
blender -b DIR/driver.blend --python preview.py -- <previewdir> none --cockpit <cockpit_X.dcm> <X.cfg>
python drm_io.py driver.drm driver_anims.dra     # format check, ends with RESULT: OK
```

`preview.py --cockpit` reads DriverCam's cockpit and car setup read-only. It seats the driver with the same fit rules
the plugin uses: eye, wheel and seat, then the reach order (grip angle, clavicle, lean).

## Files

| File | What it is |
|---|---|
| `build_driver.py` | the mesh, UVs, atlases, armature and weights, LOD0/LOD1, the seated solve and the export |
| `drm_io.py` | `.drm` / `.dra` writer, reader and checker (plain Python); the format is in its docstring |
| `preview.py` | renders and an overlap report for the seated pose |
| `driver.drm`, `driver_anims.dra` | runtime model and clips (`seated_base`, `breathe_add`) |
| `driver.fbx` | for Unreal Engine: one skeletal mesh (body + helmet + visor, 3 materials), actions `A_Pose`, `Seated_Drive`, `Seated_Breathe` |
| `driver_atlas.png`, `driver_emit.png`, `driver_helmet.png`, `driver_helmet_emit.png` | palette atlases (base colour and emission) |
| `driver_sockets.json` | sockets (they are not bones): `eye_c`, `hip_c`, `helmet`, `grip_l/r`, `heel_l/r`, `toe_l/r` |

## Conventions

- Blender: the character faces -Y, Z is up, units are metres and the character's left is +X. The armature is named
  `Armature` and its top bone is `root`, so UE adds no extra root.
- Skeleton: 55 bones with UE names. The A-pose is the rest pose.
- Unity: `C = [[-1,0,0],[0,0,1],[0,-1,0]]`. Positions use `p_u = C p`, bone matrices `C M Cᵀ`. Because det C = -1,
  the triangles are written in flipped order, as DriverCam's cockpit export does.
- Grip socket: +Z points from the pinky to the index finger (align it with the rim tangent, toward 12 o'clock).
  +Y is the palm normal: at the rim it points mostly at the hub, a little toward the dash.
- Driver-view variant: drop every triangle that has a vertex with `MASK` bit0 set (neck and collar). The torso has
  a neck plug, so no hole shows.
