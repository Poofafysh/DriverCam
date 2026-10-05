// FRogueLinkLink: the shared memory, the frame event and the shared texture ring (no UObject; owned by
// URogueLinkSubsystem). Game thread: Open / WaitFrame / ReadGame / Heartbeat. Render (RHI) thread: CopyToRing.
#pragma once

#include "CoreMinimal.h"
#include "HAL/CriticalSection.h"
#include "RogueLinkProtocol.h"

DECLARE_LOG_CATEGORY_EXTERN(LogRogueLink, Log, All);

class ROGUELINK_API FRogueLinkLink : public TSharedFromThis<FRogueLinkLink, ESPMode::ThreadSafe>
{
public:
	~FRogueLinkLink();

	/** Opens (or creates) the mapping and the frame event. */
	bool Open(FString& OutError);
	void Close();
	bool IsOpen() const { return View != nullptr; }

	/** Waits for the game's frame event. True = the game signalled a new frame. */
	bool WaitFrame(uint32 TimeoutMs);

	/** Seqlock copy of the game block. False = torn or no game data (magic / version). */
	bool ReadGame(RogueLink::FGameBlock& Out) const;

	/** Game thread, once a UE frame: heartbeat and timings into the Unreal block. */
	void Heartbeat(uint32 UeFrame, float FrameMs, float GpuMs, float WaitMs, uint32 LastGameFrame);

	/** Game thread: where the frame's work started (for WorkMs). */
	void MarkWake() { WakeSeconds = FPlatformTime::Seconds(); }

	/**
	 * Render / RHI thread (inside RHICmdList.EnqueueLambda): copies the render target's native ID3D11Texture2D into the
	 * next free slot of the shared ring (created or re-created to match its size and format), then publishes the slot.
	 */
	void CopyToRing(void* NativeSourceTexture, uint32 GameFrame);

	/** Last error from the render thread (logged once by the caller). */
	FString TakeRenderError();

	int32 GetRingWidth() const { return RingW; }
	int32 GetRingHeight() const { return RingH; }

private:
	void WriteUe(TFunctionRef<void(RogueLink::FUeBlock&)> Edit);
	void ReleaseRing();
	bool CreateRing(void* Device, int32 W, int32 H, uint32 Format);

	void* Map = nullptr;      // HANDLE
	void* Event = nullptr;    // HANDLE
	uint8* View = nullptr;
	FCriticalSection UeLock;  // the Unreal block has two writers (game thread heartbeat, render thread publish)

	// ring (render thread only, except the size readers)
	void* RingTex[RogueLink::MaxSlots] = {};
	void* RingMutex[RogueLink::MaxSlots] = {};
	uint64 RingHandle[RogueLink::MaxSlots] = {};
	int32 RingW = 0, RingH = 0;
	uint32 RingFormat = 0;
	int32 ReadySlot = -1;
	uint32 HandleGen = 0;     // seeded from the process id in Open (unique per Unreal process)

	double WakeSeconds = 0;
	FCriticalSection ErrLock;
	FString RenderError;
};
