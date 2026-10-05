"""Unreal Engine 5.8 (headless): builds the Driver's animation clips as AnimSequences on its own skeleton and exports
them as FBX. The motion is defined in anim_clips.py (original keyframes; no Mannequin, no retargeted Epic samples, no
store content).

    UnrealEditor-Cmd.exe <project>.uproject -run=pythonscript -script=<this file> -unattended -nosplash -nullrhi
    (env DRIVER_ANIM_OUT = folder for the FBX files and clips.json; default <project>/Export)
    (env DRIVER_MODEL_DIR = this model folder. UE cannot load a -script path with spaces: copy this file to a folder
     without spaces and point DRIVER_MODEL_DIR here; the imports and driver.fbx are read from it)

The project needs the Python Editor Script Plugin (and Editor Scripting Utilities). Steps:
1. imports driver.fbx (skeletal mesh + its actions A_Pose, Seated_Drive, Seated_Breathe) into /Game/Driver (once);
2. per clip: evaluates it on the skeleton's reference pose (standing clips) or on Seated_Drive frame 0 (seated clips) in
   Unreal's own bone space, writes every bone's keys into /Game/Driver/Anims/A_<clip> (frame rate and length from the
   clip), saves it and exports A_<clip>.fbx;
3. writes clips.json (name, base, mode, loop, fps, frames) for fbx_to_dra.py.
"""
import json, math, os, sys
import unreal

HERE = os.environ.get("DRIVER_MODEL_DIR") or os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import drm_io, anim_clips as A   # noqa: E402

L = unreal.log
FBX = os.path.join(HERE, "driver.fbx")
PROJECT = unreal.Paths.convert_relative_path_to_full(unreal.Paths.project_dir())
OUT = os.environ.get("DRIVER_ANIM_OUT") or os.path.join(PROJECT, "Export")
ROOT, ANIMS = "/Game/Driver", "/Game/Driver/Anims"


def import_driver():
    eal = unreal.EditorAssetLibrary
    if eal.does_asset_exist(ROOT + "/driver_Skeleton") and eal.does_asset_exist(ROOT + "/driverArmature_Seated_Drive"):
        return
    task = unreal.AssetImportTask()
    task.filename = FBX; task.destination_path = ROOT; task.automated = True; task.replace_existing = True; task.save = True
    ui = unreal.FbxImportUI()
    ui.import_mesh = True; ui.import_as_skeletal = True; ui.import_animations = True
    ui.import_materials = True; ui.import_textures = False
    ui.mesh_type_to_import = unreal.FBXImportType.FBXIT_SKELETAL_MESH
    ui.skeletal_mesh_import_data.set_editor_property("import_morph_targets", False)
    ui.anim_sequence_import_data.set_editor_property("import_bone_tracks", True)
    task.options = ui
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
    L("DRIVER_ANIM imported %s" % list(task.imported_object_paths))


def local_pose(pose, names):
    lp, lq = [], []
    for n in names:
        t = unreal.AnimPoseExtensions.get_bone_pose(pose, n, unreal.AnimPoseSpaces.LOCAL)
        lp.append((t.translation.x, t.translation.y, t.translation.z))
        r = t.rotation
        lq.append((r.x, r.y, r.z, r.w))
    return lp, lq


def make_sequence(name, skeleton, mesh):
    path = "%s/A_%s" % (ANIMS, name)
    if unreal.EditorAssetLibrary.does_asset_exist(path): unreal.EditorAssetLibrary.delete_asset(path)
    fac = unreal.AnimSequenceFactory()
    fac.set_editor_property("target_skeleton", skeleton)
    fac.set_editor_property("preview_skeletal_mesh", mesh)
    return unreal.AssetToolsHelpers.get_asset_tools().create_asset("A_" + name, ANIMS, unreal.AnimSequence, fac)


def write_keys(seq, names, fps, poses):
    """poses: [(lp, lq)] one per key (the last key is the end of the clip)."""
    c = seq.controller
    c.open_bracket("Driver clip keys", False)
    c.set_frame_rate(unreal.FrameRate(int(round(fps)), 1), False)
    c.set_number_of_frames(unreal.FrameNumber(len(poses) - 1), False)
    c.remove_all_bone_tracks(False)
    for i, n in enumerate(names):
        c.add_bone_curve(n, False)
        pos = [unreal.Vector(*p[0][i]) for p in poses]
        rot = [unreal.Quat(*p[1][i]) for p in poses]
        scl = [unreal.Vector(1.0, 1.0, 1.0)] * len(poses)
        c.set_bone_track_keys(n, pos, rot, scl, False)
    c.close_bracket(False)


def export_fbx(seq, path):
    task = unreal.AssetExportTask()
    task.object = seq; task.filename = path; task.automated = True; task.prompt = False
    task.replace_identical = True; task.exporter = unreal.AnimSequenceExporterFBX()
    opt = unreal.FbxExportOption()
    opt.ascii = False; opt.export_morph_targets = False; opt.export_preview_mesh = False
    opt.map_skeletal_motion_to_root = False; opt.export_local_time = True
    task.options = opt
    return unreal.Exporter.run_asset_export_task(task)


def main():
    os.makedirs(OUT, exist_ok=True)
    import_driver()
    skeleton = unreal.load_asset(ROOT + "/driver_Skeleton")
    mesh = unreal.load_asset(ROOT + "/driver")
    seated_seq = unreal.load_asset(ROOT + "/driverArmature_Seated_Drive")
    d = drm_io.read_drm(os.path.join(HERE, "driver.drm"))
    names = [b[0] for b in d["skel"]]; parents = [b[1] for b in d["skel"]]
    ref = unreal.AnimPoseExtensions.get_reference_pose(skeleton)
    ue_names = [str(n) for n in unreal.AnimPoseExtensions.get_bone_names(ref)]
    missing = [n for n in names if n not in ue_names]
    if missing: raise RuntimeError("bones missing in the Unreal skeleton: %s" % missing)
    rest_lp, rest_lq = local_pose(ref, names)
    opts = unreal.AnimPoseEvaluationOptions()
    seated = unreal.AnimPoseExtensions.get_anim_pose_at_frame(seated_seq, 0, opts)
    seat_lp, seat_lq = local_pose(seated, names)
    # metres per Unreal unit, measured (the imported skeleton's scale depends on the FBX unit settings, so no fixed 0.01)
    du = A.Skel(names, parents, [b[2] for b in d["skel"]], [b[3] for b in d["skel"]])
    span = lambda s_: (lambda wp: math.sqrt(sum((a - b) ** 2 for a, b in zip(wp[s_.idx["head"]], wp[s_.idx["pelvis"]]))))(
        s_.fk(s_.rest_lp, s_.rest_lq)[0])
    sk = A.Skel(names, parents, rest_lp, rest_lq, unit=1.0)
    sk.unit = span(du) / span(sk)
    L("DRIVER_ANIM axes R %s U %s F %s, %.5f m per unit" % (tuple(tuple(round(x, 3) for x in a) for a in sk.axes) + (sk.unit,)))
    manifest = []
    for clip in A.CLIPS:
        blp, blq = (seat_lp, seat_lq) if clip["base"] == "seated" else (rest_lp, rest_lq)
        n = A.frames(clip)
        keys = n + 1 if clip.get("loop") else n          # a loop gets its closing key (= frame 0) in Unreal
        poses = [A.pose(clip, f / clip["fps"], sk, blp, blq) for f in range(keys)]
        seq = make_sequence(clip["name"], skeleton, mesh)
        write_keys(seq, names, clip["fps"], poses)
        unreal.EditorAssetLibrary.save_loaded_asset(seq)
        path = os.path.join(OUT, "A_%s.fbx" % clip["name"])
        ok = export_fbx(seq, path)
        L("DRIVER_ANIM clip %-14s %3d keys %4.1f fps -> %s (%s)" % (clip["name"], keys, clip["fps"], path, "ok" if ok else "EXPORT FAILED"))
        if not ok: raise RuntimeError("FBX export failed: " + clip["name"])
        manifest.append(dict(name=clip["name"], base=clip["base"], mode=clip["mode"], loop=bool(clip.get("loop")),
                             fps=clip["fps"], frames=n, fbx=os.path.basename(path)))
    json.dump(dict(generator="ue_anims.py", engine=unreal.SystemLibrary.get_engine_version(), clips=manifest),
              open(os.path.join(OUT, "clips.json"), "w"), indent=1)
    L("DRIVER_ANIM RESULT: OK (%d clips)" % len(manifest))


try:
    main()
except Exception as e:
    import traceback
    unreal.log_error("DRIVER_ANIM RESULT: FAIL %s\n%s" % (e, traceback.format_exc()))
