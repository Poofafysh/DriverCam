using UnrealBuildTool;

public class RogueDriverAnimEditorTarget : TargetRules
{
	public RogueDriverAnimEditorTarget(TargetInfo Target) : base(Target)
	{
		Type = TargetType.Editor;
		DefaultBuildSettings = BuildSettingsVersion.Latest;
		IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
		ExtraModuleNames.Add("RogueDriverAnim");
	}
}
