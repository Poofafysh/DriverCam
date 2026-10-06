// A CMC character that walks the driver avatar around the test grid with WASD + mouse, so the locomotion AnimBP can be
// tuned with no game running (design doc 1.2: the real avatar never simulates movement; this pawn exists only for the
// UE test map). Legacy axis bindings (MoveForward / MoveRight / Turn / LookUp) so it needs no Enhanced Input assets;
// Config/DefaultInput.ini in the project (added by unreallink-start.ps1 -Setup) supplies the mappings.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Character.h"
#include "RogueDriverTestPawn.generated.h"

UCLASS()
class ROGUEDRIVERANIM_API ARogueDriverTestPawn : public ACharacter
{
	GENERATED_BODY()

public:
	ARogueDriverTestPawn();

protected:
	virtual void SetupPlayerInputComponent(class UInputComponent* InputComponent) override;

	UPROPERTY(VisibleAnywhere, Category = "Camera")
	TObjectPtr<class USpringArmComponent> SpringArm = nullptr;

	UPROPERTY(VisibleAnywhere, Category = "Camera")
	TObjectPtr<class UCameraComponent> Camera = nullptr;

	void MoveForward(float Value);
	void MoveRight(float Value);
};
