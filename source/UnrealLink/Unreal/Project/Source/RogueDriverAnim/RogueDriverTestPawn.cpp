#include "RogueDriverTestPawn.h"

#include "Camera/CameraComponent.h"
#include "Components/CapsuleComponent.h"
#include "Components/SkeletalMeshComponent.h"
#include "Engine/SkeletalMesh.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "GameFramework/SpringArmComponent.h"
#include "UObject/ConstructorHelpers.h"

ARogueDriverTestPawn::ARogueDriverTestPawn()
{
	PrimaryActorTick.bCanEverTick = true;

	GetCapsuleComponent()->InitCapsuleSize(34.f, 90.f);

	bUseControllerRotationPitch = false;
	bUseControllerRotationYaw = false;
	bUseControllerRotationRoll = false;

	if (UCharacterMovementComponent* Move = GetCharacterMovement())
	{
		Move->bOrientRotationToMovement = true;
		Move->RotationRate = FRotator(0.f, 540.f, 0.f);
		Move->MaxWalkSpeed = 160.f;             // matches URogueDriverAnimInstance::WalkSpeed (walk clip, not run)
		Move->BrakingDecelerationWalking = 1600.f;
	}

	SpringArm = CreateDefaultSubobject<USpringArmComponent>(TEXT("SpringArm"));
	SpringArm->SetupAttachment(RootComponent);
	SpringArm->TargetArmLength = 320.f;
	SpringArm->SocketOffset = FVector(0.f, 0.f, 90.f);
	SpringArm->bUsePawnControlRotation = true;

	Camera = CreateDefaultSubobject<UCameraComponent>(TEXT("Camera"));
	Camera->SetupAttachment(SpringArm, USpringArmComponent::SocketName);

	// our avatar on the mesh; the AnimBP (ABP_Driver, parent class URogueDriverAnimInstance) is assigned in the editor
	if (USkeletalMeshComponent* MeshComp = GetMesh())
	{
		static ConstructorHelpers::FObjectFinder<USkeletalMesh> Avatar(TEXT("/Game/Driver/driver.driver"));
		if (Avatar.Succeeded())
		{
			MeshComp->SetSkeletalMeshAsset(Avatar.Object);
		}
		// the clips key every bone scale=1, zeroing the skeleton root's ~100x ref scale, so the pose evaluates at metre
		// size; scale the mesh 100x to compensate (human size in the cm capsule world), drop the feet to the capsule
		// bottom and face +X. (M0 follow-up: a true cm re-import via build_driver.py --ue removes the 100x.)
		MeshComp->SetRelativeScale3D(FVector(100.f));
		MeshComp->SetRelativeLocation(FVector(0.f, 0.f, -90.f));
		MeshComp->SetRelativeRotation(FRotator(0.f, -90.f, 0.f));
	}
}

void ARogueDriverTestPawn::SetupPlayerInputComponent(UInputComponent* InInputComponent)
{
	Super::SetupPlayerInputComponent(InInputComponent);
	InInputComponent->BindAxis(TEXT("MoveForward"), this, &ARogueDriverTestPawn::MoveForward);
	InInputComponent->BindAxis(TEXT("MoveRight"), this, &ARogueDriverTestPawn::MoveRight);
	InInputComponent->BindAxis(TEXT("Turn"), this, &APawn::AddControllerYawInput);
	InInputComponent->BindAxis(TEXT("LookUp"), this, &APawn::AddControllerPitchInput);
}

void ARogueDriverTestPawn::MoveForward(float Value)
{
	if (Controller && Value != 0.f)
	{
		const FRotator Yaw(0.f, Controller->GetControlRotation().Yaw, 0.f);
		AddMovementInput(FRotationMatrix(Yaw).GetUnitAxis(EAxis::X), Value);
	}
}

void ARogueDriverTestPawn::MoveRight(float Value)
{
	if (Controller && Value != 0.f)
	{
		const FRotator Yaw(0.f, Controller->GetControlRotation().Yaw, 0.f);
		AddMovementInput(FRotationMatrix(Yaw).GetUnitAxis(EAxis::Y), Value);
	}
}
