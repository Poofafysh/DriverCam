#include "Modules/ModuleManager.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/Paths.h"
#include "ShaderCore.h"

// Maps /Plugin/RogueLink to the plugin's Shaders folder (RogueLinkAlpha.usf); needs the PostConfigInit loading phase.
class FRogueLinkModule : public IModuleInterface
{
public:
	virtual void StartupModule() override
	{
		TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("RogueLink"));
		if (Plugin.IsValid())
			AddShaderSourceDirectoryMapping(TEXT("/Plugin/RogueLink"), FPaths::Combine(Plugin->GetBaseDir(), TEXT("Shaders")));
	}
};

IMPLEMENT_MODULE(FRogueLinkModule, RogueLink)
