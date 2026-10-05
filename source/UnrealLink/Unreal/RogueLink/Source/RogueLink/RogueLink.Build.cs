using UnrealBuildTool;

public class RogueLink : ModuleRules
{
	public RogueLink(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
		PublicDependencyModuleNames.AddRange(new string[] { "Core", "CoreUObject", "Engine" });
		PrivateDependencyModuleNames.AddRange(new string[] { "RHI", "RenderCore", "Renderer", "Projects", "AnimationCore" });
		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			PublicSystemLibraries.AddRange(new string[] { "d3d11.lib", "dxgi.lib" });
		}
	}
}
