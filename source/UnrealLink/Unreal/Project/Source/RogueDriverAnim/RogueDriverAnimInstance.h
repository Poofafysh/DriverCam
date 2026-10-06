// A plain C++ UAnimInstance that holds the locomotion logic for the driver avatar in the UE test map. The AnimGraph
// (a BlendSpace idle<->walk, plus a seated additive branch) is wired in the editor on an Animation Blueprint whose
// parent class is this one; this class computes the values that graph reads every frame (thread-safe update), so the
// state logic is reviewable C++ and the editor only owns the node wiring. See source/UnrealLink/Unreal/README-anim.md.
#pragma once

#include "CoreMinimal.h"
#include "Animation/AnimInstance.h"
#include "RogueDriverAnimInstance.generated.h"

UCLASS(Blueprintable)
class ROGUEDRIVERANIM_API URogueDriverAnimInstance : public UAnimInstance
{
	GENERATED_BODY()

public:
	// speed along the ground (cm/s), for the idle<->walk blend space
	UPROPERTY(BlueprintReadOnly, Category = "Locomotion")
	float GroundSpeed = 0.f;

	// normalised 0..1 blend from idle to full walk (GroundSpeed / WalkSpeed, clamped)
	UPROPERTY(BlueprintReadOnly, Category = "Locomotion")
	float MoveBlend = 0.f;

	// true once moving faster than a small threshold (drives the idle<->walk state transition)
	UPROPERTY(BlueprintReadOnly, Category = "Locomotion")
	bool bShouldMove = false;

	// signed direction of travel relative to facing, degrees (-180..180), for strafe/turn later
	UPROPERTY(BlueprintReadOnly, Category = "Locomotion")
	float Direction = 0.f;

	// seated driving mode: the graph picks the seated additive branch instead of locomotion
	UPROPERTY(BlueprintReadWrite, Category = "State")
	bool bSeated = false;

	// the walk clip's authored ground speed (cm/s). MoveBlend = GroundSpeed / WalkSpeed.
	UPROPERTY(EditDefaultsOnly, Category = "Locomotion")
	float WalkSpeed = 160.f;

	UPROPERTY(EditDefaultsOnly, Category = "Locomotion")
	float MoveThreshold = 3.f;

	virtual void NativeInitializeAnimation() override;
	virtual void NativeThreadSafeUpdateAnimation(float DeltaSeconds) override;

private:
	UPROPERTY(Transient)
	TObjectPtr<class APawn> OwningPawn = nullptr;
};
