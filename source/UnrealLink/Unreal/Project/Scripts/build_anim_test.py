"""Build /Game/Driver/Test/L_AnimTest: a bare blueprint-grid world (grid floor + sky + sun) with the driver avatar, a
camera and the C++ ARogueDriverShotDirector. Run as an editor commandlet to (re)build and save the level:

  UnrealEditor-Cmd.exe <proj>.uproject -run=pythonscript -script=<this> -unattended -nosplash -stdout

Then render the proof stills in a real -game session (a commandlet world has no scene proxies, so it cannot render):

  set RL_RENDER_OUT=<folder>
  UnrealEditor.exe <proj>.uproject /Game/Driver/Test/L_AnimTest -game -RenderOffscreen -ResX=1000 -ResY=1000 -stdout
      -ExecCmds="t.MaxFPS 30"

The director (RogueDriverShotDirector.cpp) poses the avatar from our clips one shot at a time and writes the PNGs, then
quits. The clips key every bone scale=1, which zeroes the skeleton root's ~100x ref scale, so the pose evaluates at
metre size; the avatar actor is scaled 100 to compensate (human size in the cm world).
"""
import unreal

L = unreal.log
LEVEL = "/Game/Driver/Test/L_AnimTest"
MESH = "/Game/Driver/driver"
IDLE = "/Game/Driver/Anims/A_idle_standing"
HUMAN_SCALE = 100.0   # the skeleton's root ref bone carries a ~100x scale; the clips key every bone scale=1 (zeroing
                      # it), so the mesh poses at metre size. Actor scale 100 compensates -> human size in the cm world.
                      # (M0 follow-up: re-import at true cm via build_driver.py --ue removes this.)

eas = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
ues = unreal.get_editor_subsystem(unreal.UnrealEditorSubsystem)
les = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)


def build_level():
    if unreal.EditorAssetLibrary.does_asset_exist(LEVEL):
        les.load_level(LEVEL)
        for a in eas.get_all_level_actors():
            try:
                if a.get_actor_label().startswith("RL_"):
                    eas.destroy_actor(a)
            except Exception:
                pass
    else:
        les.new_level(LEVEL)
    world = ues.get_editor_world()

    # ground: a big plane with the engine blueprint-grid material
    floor = eas.spawn_actor_from_class(unreal.StaticMeshActor, unreal.Vector(0, 0, 0))
    floor.set_actor_label("RL_Floor")
    fsmc = floor.static_mesh_component
    fsmc.set_static_mesh(unreal.load_asset("/Engine/BasicShapes/Plane"))
    grid = unreal.load_asset("/Engine/EngineMaterials/WorldGridMaterial")
    if grid:
        fsmc.set_material(0, grid)
    floor.set_actor_scale3d(unreal.Vector(60, 60, 1))

    # lighting + sky (movable: no lighting build needed)
    sun = eas.spawn_actor_from_class(unreal.DirectionalLight, unreal.Vector(0, 0, 500))
    sun.set_actor_label("RL_Sun")
    sun.set_actor_rotation(unreal.Rotator(-48, 40, 0), False)
    sunc = sun.get_component_by_class(unreal.DirectionalLightComponent)
    sunc.set_mobility(unreal.ComponentMobility.MOVABLE)
    sunc.set_editor_property("intensity", 4.0)
    try:
        sunc.set_editor_property("atmosphere_sun_light", True)
    except Exception:
        pass

    skylight = eas.spawn_actor_from_class(unreal.SkyLight, unreal.Vector(0, 0, 500))
    skylight.set_actor_label("RL_SkyLight")
    slc = skylight.get_component_by_class(unreal.SkyLightComponent)
    slc.set_mobility(unreal.ComponentMobility.MOVABLE)
    slc.set_editor_property("real_time_capture", True)

    eas.spawn_actor_from_class(unreal.SkyAtmosphere, unreal.Vector(0, 0, 0)).set_actor_label("RL_Sky")
    eas.spawn_actor_from_class(unreal.CameraActor, unreal.Vector(-400, -400, 200)).set_actor_label("RL_PreviewCam")

    # the driver avatar: a SkeletalMeshActor so it animates in -game (the director sets the clip per shot)
    sk = eas.spawn_actor_from_class(unreal.SkeletalMeshActor, unreal.Vector(0, 0, 0))
    sk.set_actor_label("RL_Driver")
    sk.set_actor_scale3d(unreal.Vector(HUMAN_SCALE, HUMAN_SCALE, HUMAN_SCALE))
    smc = sk.skeletal_mesh_component
    try:
        smc.set_skeletal_mesh_asset(unreal.load_asset(MESH))
    except Exception:
        smc.set_skinned_asset_and_update(unreal.load_asset(MESH))
    smc.set_editor_property("visibility_based_anim_tick_option",
                            unreal.VisibilityBasedAnimTickOption.ALWAYS_TICK_POSE_AND_REFRESH_BONES)
    idle = unreal.load_asset(IDLE)
    if idle:
        smc.set_animation_mode(unreal.AnimationMode.ANIMATION_SINGLE_NODE)
        smc.set_animation(idle)

    # the shot director (C++). Placed so a -game load runs it.
    try:
        d = eas.spawn_actor_from_class(unreal.RogueDriverShotDirector, unreal.Vector(0, 0, 150))
        d.set_actor_label("RL_Director")
        L("ANIMTEST director placed")
    except Exception as e:
        L("ANIMTEST director class missing (build the RogueDriverAnim module first): %s" % e)

    # plain game mode for this map, so the RogueLink rig does not also run
    ws = world.get_world_settings()
    try:
        ws.set_editor_property("default_game_mode", unreal.GameModeBase)
    except Exception as e:
        L("ANIMTEST gamemode note: %s" % e)

    try:
        les.save_current_level()
    except Exception as e:
        L("ANIMTEST save_current_level note: %s" % e)
    if unreal.EditorAssetLibrary.does_asset_exist(LEVEL):
        unreal.EditorAssetLibrary.save_asset(LEVEL)
    L("ANIMTEST level built and saved: %s" % LEVEL)


try:
    build_level()
    L("ANIMTEST RESULT: OK")
except Exception as e:
    import traceback
    unreal.log_error("ANIMTEST RESULT: FAIL %s\n%s" % (e, traceback.format_exc()))
