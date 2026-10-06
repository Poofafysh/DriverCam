// Renders proof stills of the driver avatar in the test grid world. A pythonscript commandlet cannot render (its world
// has no scene proxies), so this runs in a real -game session (-RenderOffscreen): it poses the avatar from our clips
// one shot at a time, frames a camera on it and writes a PNG via FScreenshotRequest, then quits. Placed in L_AnimTest by
// Scripts/build_anim_test.py; the out folder comes from the RL_RENDER_OUT environment variable.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "RogueDriverShotDirector.generated.h"

USTRUCT()
struct FRogueDriverShot
{
	GENERATED_BODY()
	FString Name;
	FString Clip;
	float Time = 0.f;
	float Azimuth = 200.f;   // degrees around the avatar (180 = straight in front, looking -X)
	float Elevation = 6.f;
	float DistMult = 2.6f;
	float LookUp = 0.15f;
	bool bWheel = false;     // spawn the proxy steering wheel on the posed hands (seated shots)
};

UCLASS()
class ROGUEDRIVERANIM_API ARogueDriverShotDirector : public AActor
{
	GENERATED_BODY()

public:
	ARogueDriverShotDirector();
	virtual void BeginPlay() override;
	virtual void Tick(float DeltaSeconds) override;

private:
	void BuildShots();
	void PoseAndFrame(const FRogueDriverShot& Shot);
	void TakeShot(const FRogueDriverShot& Shot);

	UPROPERTY() TObjectPtr<class USkeletalMeshComponent> Avatar = nullptr;   // the rendered, animated mesh
	UPROPERTY() TObjectPtr<class UCameraComponent> Camera = nullptr;
	UPROPERTY() TObjectPtr<class AStaticMeshActor> Wheel = nullptr;

	TArray<FRogueDriverShot> Shots;
	FString OutDir;
	int32 Index = -1;       // -1 = warming up
	int32 Phase = 0;        // frames spent in the current sub-step
	int32 WarmupFrames = 90;
};
