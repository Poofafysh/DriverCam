// A game mode with no pawn and no HUD: the link level only shows ARogueLinkRig's preview camera.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/GameModeBase.h"
#include "RogueLinkGameMode.generated.h"

UCLASS()
class ROGUELINK_API ARogueLinkGameMode : public AGameModeBase
{
	GENERATED_BODY()

public:
	ARogueLinkGameMode();
};
