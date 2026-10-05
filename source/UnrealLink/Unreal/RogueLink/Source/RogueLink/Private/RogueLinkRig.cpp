#include "RogueLinkRig.h"

#include "RogueLinkSubsystem.h"
#include "RogueLinkLink.h"
#include "Animation/AnimSequence.h"
#include "AnimationRuntime.h"
#include "Camera/CameraComponent.h"
#include "Components/DirectionalLightComponent.h"
#include "Components/PoseableMeshComponent.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Components/SkeletalMeshComponent.h"
#include "Engine/SkeletalMesh.h"
#include "Engine/TextureRenderTarget2D.h"
#include "Engine/World.h"
#include "GameFramework/PlayerController.h"
#include "TwoBoneIK.h"

namespace
{
	const FName NPelvis("pelvis"), NHead("head"), NNeck("neck_01");
	const FName NUpper[2] = { FName("upperarm_l"), FName("upperarm_r") };
	const FName NLower[2] = { FName("lowerarm_l"), FName("lowerarm_r") };
	const FName NHand[2] = { FName("hand_l"), FName("hand_r") };
}

ARogueLinkRig::ARogueLinkRig()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.TickGroup = TG_PostUpdateWork;   // after the hidden mesh's animation

	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	RootComponent = Root;

	Driver = CreateDefaultSubobject<USkeletalMeshComponent>(TEXT("Driver"));
	Driver->SetupAttachment(Root);
	Driver->SetHiddenInGame(true);
	Driver->VisibilityBasedAnimTickOption = EVisibilityBasedAnimTickOption::AlwaysTickPoseAndRefreshBones;
	Driver->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Driver->SetCastShadow(false);

	Avatar = CreateDefaultSubobject<UPoseableMeshComponent>(TEXT("Avatar"));
	Avatar->SetupAttachment(Root);
	Avatar->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Avatar->bCastDynamicShadow = true;

	Capture = CreateDefaultSubobject<USceneCaptureComponent2D>(TEXT("Capture"));
	Capture->SetupAttachment(Root);
	Capture->bCaptureEveryFrame = false;
	Capture->bCaptureOnMovement = false;
	Capture->bAlwaysPersistRenderingState = true;
	Capture->CaptureSource = ESceneCaptureSource::SCS_FinalColorLDR;
	Capture->PrimitiveRenderMode = ESceneCapturePrimitiveRenderMode::PRM_UseShowOnlyList;
	Capture->bConsiderUnrenderedOpaquePixelAsFullyTranslucent = true;
	Capture->bOverride_CustomNearClippingPlane = true;
	Capture->CustomNearClippingPlane = 5.f;
	FEngineShowFlags& SF = Capture->ShowFlags;
	SF.SetAtmosphere(false); SF.SetFog(false); SF.SetVolumetricFog(false); SF.SetCloud(false);
	SF.SetMotionBlur(false); SF.SetBloom(false); SF.SetEyeAdaptation(false); SF.SetLensFlares(false);
	SF.SetAntiAliasing(false); SF.SetTemporalAA(false); SF.SetGrain(false); SF.SetVignette(false);
	SF.SetAmbientOcclusion(false); SF.SetScreenSpaceReflections(false); SF.SetLumenGlobalIllumination(false);
	SF.SetLumenReflections(false); SF.SetSkyLighting(false);
	FPostProcessSettings& PP = Capture->PostProcessSettings;
	PP.bOverride_AutoExposureMethod = true; PP.AutoExposureMethod = EAutoExposureMethod::AEM_Manual;
	PP.bOverride_AutoExposureApplyPhysicalCameraExposure = true; PP.AutoExposureApplyPhysicalCameraExposure = false;
	PP.bOverride_AutoExposureBias = true; PP.AutoExposureBias = 0.f;
	PP.bOverride_BloomIntensity = true; PP.BloomIntensity = 0.f;
	PP.bOverride_MotionBlurAmount = true; PP.MotionBlurAmount = 0.f;
	PP.bOverride_VignetteIntensity = true; PP.VignetteIntensity = 0.f;
	PP.bOverride_DynamicGlobalIlluminationMethod = true; PP.DynamicGlobalIlluminationMethod = EDynamicGlobalIlluminationMethod::None;
	PP.bOverride_ReflectionMethod = true; PP.ReflectionMethod = EReflectionMethod::None;

	Preview = CreateDefaultSubobject<UCameraComponent>(TEXT("Preview"));
	Preview->SetupAttachment(Root);
	Preview->PostProcessSettings = PP;
	Preview->PostProcessBlendWeight = 1.f;

	Sun = CreateDefaultSubobject<UDirectionalLightComponent>(TEXT("Sun"));
	Sun->SetupAttachment(Root);
	Sun->SetMobility(EComponentMobility::Movable);
	Sun->SetIntensity(3.14f);
	Sun->DynamicShadowCascades = 1;
	Sun->DynamicShadowDistanceMovableLight = 400.f;
	Fill = CreateDefaultSubobject<UDirectionalLightComponent>(TEXT("Fill"));
	Fill->SetupAttachment(Root);
	Fill->SetMobility(EComponentMobility::Movable);
	Fill->SetIntensity(1.f);
	Fill->SetCastShadows(false);

	AvatarMesh = TSoftObjectPtr<USkeletalMesh>(FSoftObjectPath(TEXT("/Game/Driver/driver.driver")));
	SeatedIdle = TSoftObjectPtr<UAnimSequence>(FSoftObjectPath(TEXT("/Game/Driver/Anims/A_idle_seated.A_idle_seated")));
}

void ARogueLinkRig::BeginPlay()
{
	Super::BeginPlay();
	USkeletalMesh* Mesh = AvatarMesh.LoadSynchronous();
	UAnimSequence* Idle = SeatedIdle.LoadSynchronous();
	if (!Mesh)
	{
		UE_LOG(LogRogueLink, Error, TEXT("RogueLink rig: avatar mesh %s not found; nothing to draw"), *AvatarMesh.ToString());
	}
	else
	{
		Driver->SetSkeletalMeshAsset(Mesh);
		Avatar->SetSkinnedAssetAndUpdate(Mesh);
		if (Idle) Driver->PlayAnimation(Idle, true);
		else UE_LOG(LogRogueLink, Warning, TEXT("RogueLink rig: clip %s not found; the avatar keeps its reference pose"), *SeatedIdle.ToString());
		bMeshOk = MeasureMesh();
	}
	Capture->ShowOnlyComponents.Reset();
	Capture->ShowOnlyComponents.Add(Avatar);
	EnsureTarget(IdleRenderSize);
	if (APlayerController* PC = GetWorld()->GetFirstPlayerController()) PC->SetViewTarget(this);
	UE_LOG(LogRogueLink, Log, TEXT("RogueLink rig ready: avatar %s, clip %s, %.0f cm per mesh unit"),
		Mesh ? *Mesh->GetName() : TEXT("(none)"), Idle ? *Idle->GetName() : TEXT("(none)"), CmPerUnit);
}

void ARogueLinkRig::EndPlay(const EEndPlayReason::Type Reason)
{
	Super::EndPlay(Reason);
}

bool ARogueLinkRig::MeasureMesh()
{
	USkeletalMesh* Mesh = Cast<USkeletalMesh>(Avatar->GetSkinnedAsset());
	if (!Mesh) return false;
	const FReferenceSkeleton& Ref = Mesh->GetRefSkeleton();
	const int32 P = Ref.FindBoneIndex(NPelvis), H = Ref.FindBoneIndex(NHead);
	const int32 L = Ref.FindBoneIndex(NUpper[0]), R = Ref.FindBoneIndex(NUpper[1]);
	if (P == INDEX_NONE || H == INDEX_NONE || L == INDEX_NONE || R == INDEX_NONE)
	{
		UE_LOG(LogRogueLink, Error, TEXT("RogueLink rig: the mesh has no pelvis / head / upperarm_l / upperarm_r bone"));
		return false;
	}
	const FVector Pp = FAnimationRuntime::GetComponentSpaceTransformRefPose(Ref, P).GetLocation();
	const FVector Hp = FAnimationRuntime::GetComponentSpaceTransformRefPose(Ref, H).GetLocation();
	const FVector Lp = FAnimationRuntime::GetComponentSpaceTransformRefPose(Ref, L).GetLocation();
	const FVector Rp = FAnimationRuntime::GetComponentSpaceTransformRefPose(Ref, R).GetLocation();
	const FVector Up = (Hp - Pp).GetSafeNormal();
	const FVector Right = (Rp - Lp).GetSafeNormal();
	const FVector Fwd = FVector::CrossProduct(Right, Up).GetSafeNormal();   // Unreal: X = Y x Z
	MeshToWorld = FRotationMatrix::MakeFromXZ(Fwd, Up).ToQuat().Inverse();
	RootBone = Ref.GetBoneName(0);
	RootScale = Ref.GetRefBonePose()[0].GetScale3D();
	const double Span = (Hp - Pp).Size();      // pelvis to head, about 0.70 m on the Driver character
	CmPerUnit = Span < 5.0 ? 100.0 : 1.0;
	UE_LOG(LogRogueLink, Log, TEXT("RogueLink rig: pelvis-head %.3f mesh units (-> %.0f cm per unit), mesh forward %s up %s, root '%s' scale %s"),
		Span, CmPerUnit, *Fwd.ToCompactString(), *Up.ToCompactString(), *RootBone.ToString(), *RootScale.ToCompactString());
	return true;
}

void ARogueLinkRig::EnsureTarget(FIntPoint Size)
{
	Size.X = FMath::Clamp(Size.X, 64, 3840);
	Size.Y = FMath::Clamp(Size.Y, 64, 2160);
	if (Target && Target->SizeX == Size.X && Target->SizeY == Size.Y) return;
	if (!Target)
	{
		Target = NewObject<UTextureRenderTarget2D>(this, TEXT("RogueLinkTarget"));
		Target->RenderTargetFormat = ETextureRenderTargetFormat::RTF_RGBA8;
		Target->ClearColor = FLinearColor(0, 0, 0, 0);
		Target->bAutoGenerateMips = false;
	}
	Target->InitCustomFormat(Size.X, Size.Y, PF_B8G8R8A8, false);
	Target->UpdateResourceImmediate(true);
	Capture->TextureTarget = Target;
	UE_LOG(LogRogueLink, Log, TEXT("RogueLink rig: render target %d x %d"), Size.X, Size.Y);
}

FRogueLinkState ARogueLinkRig::TestState() const
{
	// no game: a typical left-hand-drive seat and a 3/4 view from the front right, so the window and the test tool see
	// the avatar (Unreal body frame, cm)
	FRogueLinkState S;
	S.bLive = true; S.bSeatValid = true; S.bVisible = true;
	S.Eye = FVector(-55, -36, 118);
	S.SeatCushion = FVector(-45, -36, 45);
	S.SeatBack = FVector(-75, -36, 45);
	S.WheelPivot = FVector(-2, -36, 98);
	S.WheelQuat = FQuat(FVector(0, 1, 0), FMath::DegreesToRadians(20.0));   // column tilted, top toward the driver
	S.RimRadiusCm = 18.5f;
	S.SteerAngleDeg = 120.f;
	const double T = FPlatformTime::Seconds();
	S.Steer = (float)(0.4 * FMath::Sin(T * 1.3));
	S.WheelSpinDeg = -S.Steer * S.SteerAngleDeg;
	S.HeadYaw = (float)(25.0 * FMath::Sin(T * 0.7));
	const FVector CamPos(110, 70, 140), Look(-40, -36, 90);
	S.Camera = FTransform(FRotationMatrix::MakeFromXZ(Look - CamPos, FVector::UpVector).ToQuat(), CamPos);
	S.VerticalFovDeg = 50.f;
	S.Aspect = (float)IdleRenderSize.X / (float)FMath::Max(1, IdleRenderSize.Y);
	S.HorizontalFovDeg = FMath::RadiansToDegrees(2.0 * FMath::Atan(FMath::Tan(FMath::DegreesToRadians(25.0)) * S.Aspect));
	S.NearCm = 5.f;
	S.RenderSize = IdleRenderSize;
	S.SunDirection = FVector(-0.4, 0.5, -0.75).GetSafeNormal();
	S.SunColor = FLinearColor(1.f, 0.96f, 0.9f);
	S.Ambient = FLinearColor(0.35f, 0.38f, 0.42f);
	return S;
}

void ARogueLinkRig::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	URogueLinkSubsystem* Sub = GetWorld()->GetSubsystem<URogueLinkSubsystem>();
	if (!Sub || !Sub->GetLink()) return;
	const FRogueLinkState& Game = Sub->GetState();
	const bool bTest = !Game.bGameConnected;
	const FRogueLinkState S = bTest ? TestState() : Game;

	// render only while the game shows the layer (F_Visible: on, not hidden by the hotkey, a view that shows it), so a
	// hidden layer costs the shared GPU nothing
	const bool bWanted = bTest || (S.bLive && S.bVisible) || bCaptureWhenNotLive;
	// v1: car seats only (a Bikes motorcycle has no DriverCam wheel / seat for this rig yet)
	const bool bShow = bMeshOk && S.bSeatValid && !S.bBike && (bTest || S.bVisible);
	if (bShow != bWasVisible)
	{
		bWasVisible = bShow;
		UE_LOG(LogRogueLink, Log, TEXT("RogueLink rig: avatar %s%s"), bShow ? TEXT("shown") : TEXT("hidden"),
			bShow ? TEXT("") : (!bMeshOk ? TEXT(" (no mesh)") : S.bBike ? TEXT(" (bike: not supported in v1)") : !S.bSeatValid ? TEXT(" (no DriverCam seat for this car yet)") : TEXT(" (game layer off)")));
	}
	Avatar->SetVisibility(bShow);
	if (!bWanted) return;
	// no avatar to draw (bike, no seat yet, no mesh): one empty capture so the game's last picture holds no avatar,
	// then nothing until the avatar is shown again
	if (!bShow && bPublishedEmpty) return;

	// camera: the game's pose, FOV and near plane; render size as the game asks
	EnsureTarget(S.RenderSize.X > 0 ? S.RenderSize : IdleRenderSize);
	Capture->SetRelativeTransform(FTransform(S.Camera.GetRotation(), S.Camera.GetLocation()));
	Capture->FOVAngle = FMath::Clamp(S.HorizontalFovDeg, 5.f, 170.f);
	Capture->CustomNearClippingPlane = FMath::Clamp(S.NearCm, 1.f, 50.f);
	Preview->SetRelativeTransform(Capture->GetRelativeTransform());
	Preview->SetFieldOfView(Capture->FOVAngle);

	// light: the game's sun direction and colour, a soft fill from the ambient colour
	const FLinearColor SunC = S.SunColor;
	const float SunI = FMath::Clamp(SunC.GetLuminance(), 0.05f, 4.f);
	Sun->SetWorldRotation(S.SunDirection.Rotation());
	Sun->SetLightColor(SunI > 0.f ? SunC * (1.f / FMath::Max(SunC.R, FMath::Max(SunC.G, FMath::Max(SunC.B, 0.001f)))) : FLinearColor::White);
	Sun->SetIntensity(1.6f * SunI);
	const FLinearColor Amb = S.Ambient;
	Fill->SetWorldRotation(FVector(-S.SunDirection.X, -S.SunDirection.Y, -0.3).Rotation());
	Fill->SetIntensity(1.2f * FMath::Clamp(Amb.GetLuminance(), 0.05f, 2.f));

	if (bShow) PoseAvatar(S, bTest);

	Capture->CaptureScene();
	Sub->Publish(Target);
	bPublishedEmpty = !bShow;
	Captures++;
	const double Now = FPlatformTime::Seconds();
	if (Now - LastLog >= 10.0)
	{
		UE_LOG(LogRogueLink, Log, TEXT("RogueLink rig: %d captures in %.0f s (%s), render %d x %d, FOV %.1f, hands off the rim by at most %.1f cm"),
			Captures, LastLog > 0 ? Now - LastLog : 0.0, bTest ? TEXT("test pose, no game") : TEXT("game pose"), Target->SizeX, Target->SizeY, Capture->FOVAngle, IkErrMaxCm);
		LastLog = Now; Captures = 0; IkErrMaxCm = 0.f;
	}
}

void ARogueLinkRig::PoseAvatar(const FRogueLinkState& S, bool bTestPose)
{
	Avatar->CopyPoseFromSkeletalComponent(Driver);
	// the clips (ue_anims.py) key every bone's scale as 1, the imported skeleton's root carries the FBX unit scale:
	// put the root's reference scale back, or the whole pose shrinks
	{
		FTransform R = Avatar->GetBoneTransformByName(RootBone, EBoneSpaces::ComponentSpace);
		if (!R.GetScale3D().Equals(RootScale, 1e-4))
		{
			R.SetScale3D(RootScale);
			Avatar->SetBoneTransformByName(RootBone, R, EBoneSpaces::ComponentSpace);
		}
	}
	if (!bMeasured)
	{
		PelvisCS = Avatar->GetBoneTransformByName(NPelvis, EBoneSpaces::ComponentSpace).GetLocation();
		bMeasured = !PelvisCS.IsNearlyZero();
		if (bMeasured) UE_LOG(LogRogueLink, Log, TEXT("RogueLink rig: seated pelvis at %s mesh units"), *PelvisCS.ToCompactString());
	}

	// fit (the Driver plugin's rules): scale from the eye height over the cushion, the hips onto
	// (seat back + 13 cm forward, eye's side offset, cushion + 2 cm)
	const float Scale = FMath::Clamp((float)((S.Eye.Z - S.SeatCushion.Z) / 74.0), 0.85f, 1.3f);
	const FVector Hip(S.SeatBack.X + 13.0 * Scale, S.Eye.Y, S.SeatCushion.Z + 2.0 * Scale);
	const double K = CmPerUnit * Scale;
	const FVector Loc = Hip - MeshToWorld.RotateVector(PelvisCS * K);
	Avatar->SetRelativeTransform(FTransform(MeshToWorld, Loc, FVector(K)));

	HeadLook(S);
	if (bHandsOnWheel) { ArmIK(0, S, Scale); ArmIK(1, S, Scale); }

	if (S.bHideHead)
	{
		FTransform Ht = Avatar->GetBoneTransformByName(NHead, EBoneSpaces::ComponentSpace);
		Ht.SetScale3D(FVector(0.001));
		Avatar->SetBoneTransformByName(NHead, Ht, EBoneSpaces::ComponentSpace);
	}
}

void ARogueLinkRig::HeadLook(const FRogueLinkState& S)
{
	if (FMath::Abs(S.HeadYaw) < 0.01f && FMath::Abs(S.HeadPitch) < 0.01f) return;
	const FQuat WorldToMesh = MeshToWorld.Inverse();
	const FVector UpCS = WorldToMesh.RotateVector(FVector::UpVector);
	const FVector RightCS = WorldToMesh.RotateVector(FVector::RightVector);
	const FName Bones[2] = { NNeck, NHead };
	const float Share[2] = { 0.35f, 0.65f };
	for (int i = 0; i < 2; i++)
	{
		FTransform T = Avatar->GetBoneTransformByName(Bones[i], EBoneSpaces::ComponentSpace);
		const FQuat D = FQuat(UpCS, FMath::DegreesToRadians(S.HeadYaw * Share[i])) * FQuat(RightCS, FMath::DegreesToRadians(-S.HeadPitch * Share[i]));
		T.SetRotation(D * T.GetRotation());
		Avatar->SetBoneTransformByName(Bones[i], T, EBoneSpaces::ComponentSpace);
	}
}

void ARogueLinkRig::ArmIK(int32 Side, const FRogueLinkState& S, float Scale)
{
	// the Driver plugin's grip: the rim at 165 deg (left) / 15 deg (right) of the spun wheel, 1 cm toward the driver
	const FQuat Wq = S.WheelQuat;
	const FVector X = Wq.RotateVector(FVector(0, 1, 0)), U = Wq.RotateVector(FVector(0, 0, 1)), Fw = Wq.RotateVector(FVector(1, 0, 0));
	const double A = FMath::DegreesToRadians(FMath::Clamp((double)S.WheelSpinDeg, -100.0, 100.0));
	const FVector Xs = X * FMath::Cos(A) + U * FMath::Sin(A);
	const FVector Us = U * FMath::Cos(A) - X * FMath::Sin(A);
	const double Th = FMath::DegreesToRadians(Side == 0 ? 165.0 : 15.0);
	const FVector Radial = Xs * FMath::Cos(Th) + Us * FMath::Sin(Th);
	const FVector Grip = S.WheelPivot + Radial * S.RimRadiusCm - Fw * 1.0;
	// v1: the hand bone (wrist) a little outside the rim and toward the driver; the hand keeps the clip's rotation
	const FVector Wrist = Grip + Radial * 2.0 - Fw * 6.0;

	const FTransform C2W = Avatar->GetComponentTransform();
	FTransform Ut = Avatar->GetBoneTransformByName(NUpper[Side], EBoneSpaces::ComponentSpace);
	FTransform Lt = Avatar->GetBoneTransformByName(NLower[Side], EBoneSpaces::ComponentSpace);
	FTransform Ht = Avatar->GetBoneTransformByName(NHand[Side], EBoneSpaces::ComponentSpace);
	const FVector ShoulderW = C2W.TransformPosition(Ut.GetLocation());
	const FVector PoleW = ShoulderW + (FVector(10.0, Side == 0 ? -35.0 : 35.0, -50.0) * Scale);
	const FVector EffCS = C2W.InverseTransformPosition(Wrist);
	const FVector PoleCS = C2W.InverseTransformPosition(PoleW);
	AnimationCore::SolveTwoBoneIK(Ut, Lt, Ht, PoleCS, EffCS, true, 1.0, 1.08);
	Avatar->SetBoneTransformByName(NUpper[Side], Ut, EBoneSpaces::ComponentSpace);
	Avatar->SetBoneTransformByName(NLower[Side], Lt, EBoneSpaces::ComponentSpace);
	Avatar->SetBoneTransformByName(NHand[Side], Ht, EBoneSpaces::ComponentSpace);
	// how far the wrist ended up from its target (cm), for the periodic log
	const FVector Got = C2W.TransformPosition(Avatar->GetBoneTransformByName(NHand[Side], EBoneSpaces::ComponentSpace).GetLocation());
	IkErrMaxCm = FMath::Max(IkErrMaxCm, (float)FVector::Dist(Got, Wrist));
}
