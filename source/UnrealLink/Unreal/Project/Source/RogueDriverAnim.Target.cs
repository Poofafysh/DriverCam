using UnrealBuildTool;

// Code-project stub for RogueDriverAnim (copied in by tools/unreallink-start.ps1 -Setup): an installed engine
// builds and packages plugin C++ (RogueLink) only inside a code project.
public class RogueDriverAnimTarget : TargetRules
{
	public RogueDriverAnimTarget(TargetInfo Target) : base(Target)
	{
		Type = TargetType.Game;
		DefaultBuildSettings = BuildSettingsVersion.Latest;
		IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
		ExtraModuleNames.Add("RogueDriverAnim");
	}
}
