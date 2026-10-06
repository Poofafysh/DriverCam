#include "RogueDriverShotDirector.h"

#include "Animation/AnimSequence.h"
#include "Animation/SkeletalMeshActor.h"
#include "Camera/CameraComponent.h"
#include "Components/SkeletalMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/StaticMesh.h"
#include "Engine/World.h"
#include "GameFramework/PlayerController.h"
#include "HAL/PlatformMisc.h"
#include "Kismet/GameplayStatics.h"
#include "Materials/MaterialInterface.h"
#include "Misc/Paths.h"
#include "UnrealClient.h"

ARogueDriverShotDirector::ARogueDriverShotDirector()
{
	PrimaryActorTick.bCanEverTick = true;
	Camera = CreateDefaultSubobject<UCameraComponent>(TEXT("ShotCamera"));
	RootComponent = Camera;
}

void ARogueDriverShotDirector::BeginPlay()
{
	Super::BeginPlay();

	// the avatar = the first skeletal mesh actor in the level (built by build_anim_test.py)
	if (AActor* Found = UGameplayStatics::GetActorOfClass(GetWorld(), ASkeletalMeshActor::StaticClass()))
	{
		Avatar = Cast<ASkeletalMeshActor>(Found)->GetSkeletalMeshComponent();
	}

	OutDir = FPlatformMisc::GetEnvironmentVariable(TEXT("RL_RENDER_OUT"));
	if (OutDir.IsEmpty())
	{
		OutDir = FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("AnimTest"));
	}

	BuildShots();

	if (APlayerController* PC = UGameplayStatics::GetPlayerController(GetWorld(), 0))
	{
		PC->SetViewTarget(this);
	}
	UE_LOG(LogTemp, Display, TEXT("ANIMTEST director: avatar %s, %d shots, out=%s"),
		Avatar ? TEXT("ok") : TEXT("MISSING"), Shots.Num(), *OutDir);
}

void ARogueDriverShotDirector::BuildShots()
{
	auto Add = [&](const TCHAR* N, const TCHAR* C, float T, float Az, float El, float Dist, float Up, bool Wheel)
	{
		FRogueDriverShot S; S.Name = N; S.Clip = C; S.Time = T; S.Azimuth = Az; S.Elevation = El;
		S.DistMult = Dist; S.LookUp = Up; S.bWheel = Wheel; Shots.Add(S);
	};
	Add(TEXT("01_idle_front"),   TEXT("A_idle_standing"), 0.0f, 180, 5, 2.6f, 0.15f, false);
	Add(TEXT("02_idle_34"),      TEXT("A_idle_standing"), 0.0f, 215, 8, 2.6f, 0.15f, false);
	Add(TEXT("03_walk_pose"),    TEXT("A_walk"),          0.30f, 215, 6, 2.6f, 0.15f, false);
	Add(TEXT("04_walk_cycle_0"), TEXT("A_walk"),          0.0f,  200, 5, 2.6f, 0.15f, false);
	Add(TEXT("04_walk_cycle_1"), TEXT("A_walk"),          0.25f, 200, 5, 2.6f, 0.15f, false);
	Add(TEXT("04_walk_cycle_2"), TEXT("A_walk"),          0.50f, 200, 5, 2.6f, 0.15f, false);
	Add(TEXT("04_walk_cycle_3"), TEXT("A_walk"),          0.75f, 200, 5, 2.6f, 0.15f, false);
	Add(TEXT("05_seated_34"),    TEXT("A_idle_seated"),   0.0f,  210, 10, 2.2f, 0.10f, true);
	Add(TEXT("06_seated_front"), TEXT("A_idle_seated"),   0.0f,  180, 5, 2.2f, 0.10f, true);
}

void ARogueDriverShotDirector::PoseAndFrame(const FRogueDriverShot& Shot)
{
	if (!Avatar) return;

	// pose: single-node clip held at Shot.Time (the world ticks in -game, so the mesh actually evaluates and renders)
	const FString Path = FString::Printf(TEXT("/Game/Driver/Anims/%s.%s"), *Shot.Clip, *Shot.Clip);
	if (UAnimSequence* Seq = Cast<UAnimSequence>(StaticLoadObject(UAnimSequence::StaticClass(), nullptr, *Path)))
	{
		Avatar->PlayAnimation(Seq, false);
		Avatar->SetPosition(FMath::Clamp(Shot.Time * (float)Seq->GetPlayLength(), 0.f, (float)Seq->GetPlayLength()), false);
		Avatar->SetPlayRate(0.f);
	}

	// proxy steering wheel for the seated shots
	if (Wheel) { Wheel->Destroy(); Wheel = nullptr; }
	if (Shot.bWheel)
	{
		const FVector LH = Avatar->GetSocketLocation(FName("hand_l"));
		const FVector RH = Avatar->GetSocketLocation(FName("hand_r"));
		const FVector C = (LH + RH) * 0.5f;
		const float Rad = FMath::Max(10.f, (float)(LH - RH).Size() * 0.5f * 1.05f);
		Wheel = GetWorld()->SpawnActor<AStaticMeshActor>(C, FRotator(0.f, 90.f, 0.f));
		if (Wheel)
		{
			Wheel->SetMobility(EComponentMobility::Movable);
			UStaticMeshComponent* WC = Wheel->GetStaticMeshComponent();
			if (UStaticMesh* Cyl = Cast<UStaticMesh>(StaticLoadObject(UStaticMesh::StaticClass(), nullptr, TEXT("/Engine/BasicShapes/Cylinder.Cylinder"))))
			{
				WC->SetStaticMesh(Cyl);
			}
			WC->SetRelativeScale3D(FVector(0.08f, Rad / 50.f, Rad / 50.f));
		}
	}
}

void ARogueDriverShotDirector::TakeShot(const FRogueDriverShot& Shot)
{
	if (!Avatar) return;
	const FBoxSphereBounds B = Avatar->Bounds;
	const float R = FMath::Max(10.f, (float)B.SphereRadius);
	const FVector Look = B.Origin + FVector(0, 0, Shot.LookUp * R);
	const float D = R * Shot.DistMult;
	const float Az = FMath::DegreesToRadians(Shot.Azimuth);
	const float El = FMath::DegreesToRadians(Shot.Elevation);
	const FVector Cam = Look + FVector(D * FMath::Cos(El) * FMath::Cos(Az), D * FMath::Cos(El) * FMath::Sin(Az), D * FMath::Sin(El));
	SetActorLocation(Cam);
	SetActorRotation((Look - Cam).Rotation());
	Camera->SetFieldOfView(38.f);

	const FString File = FPaths::Combine(OutDir, Shot.Name + TEXT(".png"));
	FScreenshotRequest::RequestScreenshot(File, false, false);
	UE_LOG(LogTemp, Display, TEXT("ANIMTEST director: shot %s -> %s (R=%.1f)"), *Shot.Name, *File, R);
}

void ARogueDriverShotDirector::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	if (Index < 0)
	{
		if (--WarmupFrames > 0) return;   // let shaders compile / streaming settle
		Index = 0; Phase = 0;
	}
	if (Index >= Shots.Num())
	{
		UE_LOG(LogTemp, Display, TEXT("ANIMTEST director: RESULT OK (%d shots)"), Shots.Num());
		FPlatformMisc::RequestExit(false);
		return;
	}

	// sub-steps per shot: pose (0), let it settle (1..3), screenshot (4), wait for the file (5..11), next
	++Phase;
	if (Phase == 1)
	{
		PoseAndFrame(Shots[Index]);
	}
	else if (Phase == 4)
	{
		TakeShot(Shots[Index]);
	}
	else if (Phase >= 12)
	{
		++Index; Phase = 0;
	}
}
