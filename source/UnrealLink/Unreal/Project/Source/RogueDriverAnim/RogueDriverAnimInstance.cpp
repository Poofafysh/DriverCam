#include "RogueDriverAnimInstance.h"

#include "GameFramework/Pawn.h"
#include "GameFramework/PawnMovementComponent.h"

void URogueDriverAnimInstance::NativeInitializeAnimation()
{
	Super::NativeInitializeAnimation();
	OwningPawn = TryGetPawnOwner();
}

void URogueDriverAnimInstance::NativeThreadSafeUpdateAnimation(float DeltaSeconds)
{
	Super::NativeThreadSafeUpdateAnimation(DeltaSeconds);
	if (!OwningPawn)
	{
		return;
	}

	const FVector Velocity = OwningPawn->GetVelocity();
	const FVector Ground(Velocity.X, Velocity.Y, 0.f);
	GroundSpeed = (float)Ground.Size();
	MoveBlend = FMath::Clamp(GroundSpeed / FMath::Max(WalkSpeed, 1.f), 0.f, 1.f);
	bShouldMove = GroundSpeed > MoveThreshold;

	if (bShouldMove)
	{
		const FRotator Facing = OwningPawn->GetActorRotation();
		Direction = (float)FMath::UnwindDegrees(Ground.Rotation().Yaw - Facing.Yaw);
	}
	else
	{
		Direction = 0.f;
	}
}
