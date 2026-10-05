#include "RogueLinkGameMode.h"

#include "GameFramework/PlayerController.h"

ARogueLinkGameMode::ARogueLinkGameMode()
{
	DefaultPawnClass = nullptr;
	SpectatorClass = nullptr;
	HUDClass = nullptr;
	PlayerControllerClass = APlayerController::StaticClass();
}
