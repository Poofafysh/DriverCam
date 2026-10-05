// ARogueLinkRig: the avatar and the camera that renders it for the game.
//  - Avatar: the Driver plugin's own character (/Game/Driver/driver), posed every frame: a hidden skeletal mesh plays
//    the seated idle clip (/Game/Driver/Anims/A_idle_seated), the visible poseable mesh copies that pose, then sits
//    it on DriverCam's seat (scale from the eye height, the Driver plugin's fit rules) and puts both hands on the
//    spun wheel rim with two-bone IK. Head hidden while the camera is inside it (driver view).
//  - Capture: a SceneCapture2D at the game camera's pose / FOV / near plane, rendering only the avatar, alpha kept,
//    into a BGRA8 render target the size the game asks for; published to the game's shared texture ring.
//  - Preview: a camera at the same pose, so the small Unreal window shows the same picture (over black).
// Subclass it in Blueprint (or replace the IK with an AnimBP / Control Rig) and place it in the level: the subsystem
// then uses that one instead of spawning this.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "RogueLinkTypes.h"
#include "RogueLinkRig.generated.h"

class USkeletalMesh;
class UAnimSequence;
class USkeletalMeshComponent;
class UPoseableMeshComponent;
class USceneCaptureComponent2D;
class UCameraComponent;
class UDirectionalLightComponent;
class UTextureRenderTarget2D;

UCLASS(Blueprintable)
class ROGUELINK_API ARogueLinkRig : public AActor
{
	GENERATED_BODY()

public:
	ARogueLinkRig();

	virtual void BeginPlay() override;
	virtual void EndPlay(const EEndPlayReason::Type Reason) override;
	virtual void Tick(float DeltaSeconds) override;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "RogueLink")
	TSoftObjectPtr<USkeletalMesh> AvatarMesh;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "RogueLink")
	TSoftObjectPtr<UAnimSequence> SeatedIdle;

	/** Render size while no game is connected (preview / test). */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "RogueLink")
	FIntPoint IdleRenderSize = FIntPoint(640, 360);

	/** Hands on the wheel (two-bone IK). Off = the clip's arms. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "RogueLink")
	bool bHandsOnWheel = true;

	/** Capture even when the game is connected but not driving (menus): off saves the GPU. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "RogueLink")
	bool bCaptureWhenNotLive = false;

	UFUNCTION(BlueprintPure, Category = "RogueLink")
	UTextureRenderTarget2D* GetRenderTarget() const { return Target; }

protected:
	UPROPERTY(VisibleAnywhere, Category = "RogueLink") TObjectPtr<USceneComponent> Root;
	UPROPERTY(VisibleAnywhere, Category = "RogueLink") TObjectPtr<USkeletalMeshComponent> Driver;
	UPROPERTY(VisibleAnywhere, Category = "RogueLink") TObjectPtr<UPoseableMeshComponent> Avatar;
	UPROPERTY(VisibleAnywhere, Category = "RogueLink") TObjectPtr<USceneCaptureComponent2D> Capture;
	UPROPERTY(VisibleAnywhere, Category = "RogueLink") TObjectPtr<UCameraComponent> Preview;
	UPROPERTY(VisibleAnywhere, Category = "RogueLink") TObjectPtr<UDirectionalLightComponent> Sun;
	UPROPERTY(VisibleAnywhere, Category = "RogueLink") TObjectPtr<UDirectionalLightComponent> Fill;
	UPROPERTY(Transient) TObjectPtr<UTextureRenderTarget2D> Target;

private:
	void EnsureTarget(FIntPoint Size);
	bool MeasureMesh();
	void PoseAvatar(const FRogueLinkState& S, bool bTestPose);
	void ArmIK(int32 Side, const FRogueLinkState& S, float Scale);
	void HeadLook(const FRogueLinkState& S);
	FRogueLinkState TestState() const;

	bool bMeshOk = false, bMeasured = false;
	FQuat MeshToWorld = FQuat::Identity;  // rotates the mesh's own axes onto X forward / Y right / Z up
	double CmPerUnit = 1.0;               // mesh units -> cm (1 unit = 1 m -> 100)
	FVector PelvisCS = FVector::ZeroVector;
	FName RootBone;
	FVector RootScale = FVector::OneVector;
	double LastLog = 0;
	int32 Captures = 0;
	float IkErrMaxCm = 0.f;
	bool bWasVisible = false;
	bool bPublishedEmpty = false;          // the last published picture had no avatar (skip captures until it shows)
};
