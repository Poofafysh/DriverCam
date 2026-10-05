// URogueLinkSubsystem: one per game world. At the start of every engine frame it waits (with a timeout) for the
// game's frame event, reads the game block and converts it into FRogueLinkState; it spawns ARogueLinkRig when the
// level has none, and owns the shared memory / texture ring (FRogueLinkLink).
#pragma once

#include "CoreMinimal.h"
#include "Subsystems/WorldSubsystem.h"
#include "RogueLinkTypes.h"
#include "RogueLinkSubsystem.generated.h"

class FRogueLinkLink;
struct FRogueLinkFixTex;

UCLASS()
class ROGUELINK_API URogueLinkSubsystem : public UWorldSubsystem
{
	GENERATED_BODY()

public:
	virtual bool ShouldCreateSubsystem(UObject* Outer) const override;
	virtual void Initialize(FSubsystemCollectionBase& Collection) override;
	virtual void Deinitialize() override;
	virtual void OnWorldBeginPlay(UWorld& InWorld) override;

	/** The game's latest frame (Unreal axes, cm, body frame). */
	UFUNCTION(BlueprintPure, Category = "RogueLink")
	const FRogueLinkState& GetState() const { return State; }

	/** Short status for the preview window / log. */
	UFUNCTION(BlueprintPure, Category = "RogueLink")
	FString GetStatusLine() const { return StatusLine; }

	TSharedPtr<FRogueLinkLink, ESPMode::ThreadSafe> GetLink() const { return Link; }
	uint32 GetGameFrame() const { return (uint32)State.GameFrame; }

	/** Called by the rig after it captured: copies the render target into the shared ring on the render thread. */
	void Publish(class UTextureRenderTarget2D* Target);

private:
	void OnBeginFrame();

	TSharedPtr<FRogueLinkLink, ESPMode::ThreadSafe> Link;
	TSharedPtr<FRogueLinkFixTex, ESPMode::ThreadSafe> Fix;   // render thread: the alpha-fixed copy
	FDelegateHandle BeginFrameHandle;
	FRogueLinkState State;
	FString StatusLine;
	uint32 UeFrame = 0;
	uint32 LastSeenFrame = 0;
	double LastFrameChange = -100;
	double LastLog = 0;
	float WaitMsAvg = 0;
	int32 Frames = 0, Waited = 0, TimedOut = 0;
	bool bWasConnected = false;
};
