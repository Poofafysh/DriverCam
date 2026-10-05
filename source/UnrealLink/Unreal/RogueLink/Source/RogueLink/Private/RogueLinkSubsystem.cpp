#include "RogueLinkSubsystem.h"

#include "RogueLinkLink.h"
#include "RogueLinkRig.h"
#include "Engine/World.h"
#include "Engine/TextureRenderTarget2D.h"
#include "EngineUtils.h"
#include "Misc/CoreDelegates.h"
#include "Misc/App.h"
#include "RenderingThread.h"
#include "RHICommandList.h"
#include "DynamicRHI.h"
#include "TextureResource.h"
#include "GlobalShader.h"
#include "ShaderParameterStruct.h"
#include "RenderGraphBuilder.h"
#include "RenderGraphUtils.h"

// The scene capture's alpha is inverted (1 - opacity) by design; this pass writes (rgb, opacity) into an RGBA8
// texture with a UAV, which is what goes into the shared ring (Shaders/Private/RogueLinkAlpha.usf).
class FRogueLinkAlphaCS : public FGlobalShader
{
public:
	DECLARE_GLOBAL_SHADER(FRogueLinkAlphaCS);
	SHADER_USE_PARAMETER_STRUCT(FRogueLinkAlphaCS, FGlobalShader);
	BEGIN_SHADER_PARAMETER_STRUCT(FParameters, )
		SHADER_PARAMETER_RDG_TEXTURE(Texture2D<float4>, InTex)
		SHADER_PARAMETER_RDG_TEXTURE_UAV(RWTexture2D<float4>, OutTex)
		SHADER_PARAMETER(FUintVector2, Size)
		SHADER_PARAMETER(uint32, Encode)
	END_SHADER_PARAMETER_STRUCT()
	static bool ShouldCompilePermutation(const FGlobalShaderPermutationParameters& P)
	{
		return IsFeatureLevelSupported(P.Platform, ERHIFeatureLevel::SM5);
	}
};
IMPLEMENT_GLOBAL_SHADER(FRogueLinkAlphaCS, "/Plugin/RogueLink/Private/RogueLinkAlpha.usf", "MainCS", SF_Compute);

struct FRogueLinkFixTex
{
	FTextureRHIRef Tex;   // render thread only
};

using namespace RogueLink;

namespace
{
	// Unity (x right, y up, z forward, metres) -> Unreal (x forward, y right, z up, cm). The axis swap is a cyclic
	// permutation (a proper rotation, both left-handed), so quaternions map the same way: (qz, qx, qy, qw).
	FVector Pos(const float* V) { return FVector(V[2], V[0], V[1]) * 100.0; }
	FVector Dir(const float* V) { return FVector(V[2], V[0], V[1]); }
	FQuat Rot(const float* Q)
	{
		FQuat R(Q[2], Q[0], Q[1], Q[3]);
		return R.SizeSquared() > 1e-6 ? R.GetNormalized() : FQuat::Identity;
	}
	FString Ascii(const char* S, int Len)
	{
		int N = 0;
		while (N < Len && S[N]) N++;
		return FString::ConstructFromPtrSize(S, N);
	}
}

bool URogueLinkSubsystem::ShouldCreateSubsystem(UObject* Outer) const
{
	const UWorld* W = Cast<UWorld>(Outer);
	return W && (W->WorldType == EWorldType::Game || W->WorldType == EWorldType::PIE);
}

void URogueLinkSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
	Super::Initialize(Collection);
	if (RHIGetInterfaceType() != ERHIInterfaceType::D3D11)
	{
		UE_LOG(LogRogueLink, Error, TEXT("RogueLink needs the D3D11 renderer (start with -dx11); the game can't open this RHI's textures. Link off."));
		StatusLine = TEXT("not D3D11 (start with -dx11)");
		return;
	}
	Link = MakeShared<FRogueLinkLink, ESPMode::ThreadSafe>();
	FString Err;
	if (!Link->Open(Err))
	{
		UE_LOG(LogRogueLink, Error, TEXT("RogueLink: shared memory failed: %s"), *Err);
		Link.Reset();
		StatusLine = TEXT("shared memory failed");
		return;
	}
	BeginFrameHandle = FCoreDelegates::OnBeginFrame.AddUObject(this, &URogueLinkSubsystem::OnBeginFrame);
	StatusLine = TEXT("waiting for the game");
	UE_LOG(LogRogueLink, Log, TEXT("RogueLink 0.1.0: shared memory open, waiting for Driving Rogue (UnrealLink plugin)"));
}

void URogueLinkSubsystem::Deinitialize()
{
	if (BeginFrameHandle.IsValid()) FCoreDelegates::OnBeginFrame.Remove(BeginFrameHandle);
	BeginFrameHandle.Reset();
	if (Link)
	{
		FlushRenderingCommands();   // no CopyToRing still queued
		Link->Close();
		Link.Reset();
	}
	Super::Deinitialize();
}

void URogueLinkSubsystem::OnWorldBeginPlay(UWorld& InWorld)
{
	Super::OnWorldBeginPlay(InWorld);
	if (!Link) return;
	for (TActorIterator<ARogueLinkRig> It(&InWorld); It; ++It) return;   // the level has its own (e.g. a Blueprint child)
	FActorSpawnParameters P;
	P.Name = TEXT("RogueLinkRig");
	InWorld.SpawnActor<ARogueLinkRig>(ARogueLinkRig::StaticClass(), FTransform::Identity, P);
	UE_LOG(LogRogueLink, Log, TEXT("RogueLink: spawned ARogueLinkRig (no rig in the level)"));
}

void URogueLinkSubsystem::OnBeginFrame()
{
	if (!Link) return;
	const double Now = FPlatformTime::Seconds();
	const bool bConnected = Now - LastFrameChange < 1.0;
	// paced by the game: one Unreal frame per game frame; idle at ~10 fps while no game is running
	const double W0 = FPlatformTime::Seconds();
	const bool bSignalled = Link->WaitFrame(bConnected ? 25 : 100);
	const float WaitMs = (float)((FPlatformTime::Seconds() - W0) * 1000.0);
	Link->MarkWake();
	Frames++;
	if (bSignalled) Waited++; else TimedOut++;
	WaitMsAvg += WaitMs;

	FGameBlock G;
	if (Link->ReadGame(G))
	{
		if (G.Frame != LastSeenFrame) { LastSeenFrame = G.Frame; LastFrameChange = FPlatformTime::Seconds(); }
		FRogueLinkState& S = State;
		S.GameFrame = (int32)G.Frame;
		S.GameTime = G.Time;
		S.GameDeltaTime = G.Dt;
		S.bLive = (G.Flags & F_Live) != 0;
		S.bDriverView = (G.Flags & F_DriverView) != 0;
		S.bChaseView = (G.Flags & F_ChaseView) != 0;
		S.bBike = (G.Flags & F_Bike) != 0;
		S.bPaused = (G.Flags & F_Paused) != 0;
		S.bSeatValid = (G.Flags & F_DriverCamValid) != 0;
		S.bHideHead = (G.Flags & F_HideHead) != 0;
		S.bVisible = (G.Flags & F_Visible) != 0;
		S.Camera = FTransform(Rot(G.Cam + 3), Pos(G.Cam));
		S.CameraWorld = FTransform(Rot(G.CamWorld + 3), Pos(G.CamWorld));
		S.VerticalFovDeg = G.VFov;
		S.Aspect = G.Aspect > 0.1f ? G.Aspect : 16.f / 9.f;
		S.HorizontalFovDeg = FMath::RadiansToDegrees(2.0 * FMath::Atan(FMath::Tan(FMath::DegreesToRadians(G.VFov * 0.5)) * S.Aspect));
		S.NearCm = FMath::Max(1.f, G.Near * 100.f);
		S.RenderSize = FIntPoint(G.WantW, G.WantH);
		S.BodyWorld = FTransform(Rot(G.Body + 3), Pos(G.Body));
		S.Velocity = Pos(G.Vel);
		S.AngularVelocity = Dir(G.AngVel);
		S.Steer = G.Input[0]; S.Throttle = G.Input[1]; S.Brake = G.Input[2]; S.SpeedMps = G.Input[3];
		S.SunDirection = Dir(G.SunDir).GetSafeNormal(UE_SMALL_NUMBER, FVector(0, 0, -1));
		S.SunColor = FLinearColor(G.SunColor[0], G.SunColor[1], G.SunColor[2]);
		S.Ambient = FLinearColor(G.Ambient[0], G.Ambient[1], G.Ambient[2]);
		S.HeadYaw = G.HeadLook[2] > 0.5f ? G.HeadLook[0] : 0.f;
		S.HeadPitch = G.HeadLook[2] > 0.5f ? G.HeadLook[1] : 0.f;
		S.Rpm = G.Engine[0]; S.Gear = (int32)G.Engine[3]; S.bEngineLive = G.Engine[4] > 0.5f;
		const float* D = G.DriverCam;
		S.Eye = Pos(D + 5);
		S.WheelPivot = Pos(D + 8);
		S.WheelQuat = Rot(D + 11);
		S.WheelRotation = S.WheelQuat.Rotator();
		S.RimRadiusCm = D[15] * 100.f;
		S.SteerAngleDeg = D[23] > 1.f ? D[23] : 120.f;
		S.WheelSpinDeg = -S.Steer * S.SteerAngleDeg;
		S.SeatCushion = Pos(D + 17);
		S.SeatBack = Pos(D + 20);
		S.CarId = Ascii(G.Car, sizeof(G.Car));
		S.BikeKey = Ascii(G.BikeKey, sizeof(G.BikeKey));
	}
	State.bGameConnected = FPlatformTime::Seconds() - LastFrameChange < 1.0;
	if (State.bGameConnected != bWasConnected)
	{
		bWasConnected = State.bGameConnected;
		UE_LOG(LogRogueLink, Log, TEXT("RogueLink: game %s"), bWasConnected ? TEXT("connected") : TEXT("gone (idling at ~10 fps)"));
	}

	UeFrame++;
	const float FrameMs = (float)(FApp::GetDeltaTime() * 1000.0);
	const float GpuMs = (float)FPlatformTime::ToMilliseconds(RHIGetGPUFrameCycles(0));
	Link->Heartbeat(UeFrame, FrameMs, GpuMs, WaitMs, LastSeenFrame);

	const FString RenderErr = Link->TakeRenderError();
	if (!RenderErr.IsEmpty()) UE_LOG(LogRogueLink, Error, TEXT("RogueLink render thread: %s"), *RenderErr);

	if (Now - LastLog >= 5.0)
	{
		const float AvgWait = Frames > 0 ? WaitMsAvg / Frames : 0.f;
		StatusLine = FString::Printf(TEXT("%s, frame %d, %.0f fps, GPU %.2f ms, wait %.2f ms"),
			State.bGameConnected ? TEXT("game connected") : TEXT("waiting for the game"), State.GameFrame,
			FrameMs > 0.f ? 1000.f / FrameMs : 0.f, GpuMs, AvgWait);
		if (State.bGameConnected || LastLog == 0)
			UE_LOG(LogRogueLink, Log, TEXT("RogueLink [Perf] %d frames in %.1f s: %d paced by the game, %d timed out; frame %.2f ms, GPU %.2f ms, avg wait %.2f ms; render %d x %d"),
				Frames, Now - LastLog, Waited, TimedOut, FrameMs, GpuMs, AvgWait, Link->GetRingWidth(), Link->GetRingHeight());
		LastLog = Now; Frames = Waited = TimedOut = 0; WaitMsAvg = 0;
	}
}

void URogueLinkSubsystem::Publish(UTextureRenderTarget2D* Target)
{
	if (!Link || !Target) return;
	FTextureRenderTargetResource* Res = Target->GameThread_GetRenderTargetResource();
	if (!Res) return;
	if (!Fix) Fix = MakeShared<FRogueLinkFixTex, ESPMode::ThreadSafe>();
	TSharedPtr<FRogueLinkLink, ESPMode::ThreadSafe> L = Link;
	TSharedPtr<FRogueLinkFixTex, ESPMode::ThreadSafe> X = Fix;
	// a test-pose capture (no game connected) is stamped frame 0, which the game never copies: State.GameFrame is
	// still the last game frame then, and the game would take the test pose for its own after a hitch / reconnect
	const uint32 F = State.bGameConnected ? (uint32)State.GameFrame : 0u;
	ENQUEUE_RENDER_COMMAND(RogueLinkPublish)([L, X, Res, F](FRHICommandListImmediate& RHICmdList)
	{
		FTextureRHIRef Src = Res->GetRenderTargetTexture();
		if (!Src.IsValid()) return;
		const FIntPoint Size = Src->GetSizeXY();
		if (!X->Tex.IsValid() || X->Tex->GetSizeXY() != Size)
		{
			const FRHITextureCreateDesc Desc = FRHITextureCreateDesc::Create2D(TEXT("RogueLinkOut"), Size.X, Size.Y, PF_R8G8B8A8)
				.SetFlags(ETextureCreateFlags::UAV | ETextureCreateFlags::ShaderResource)
				.SetInitialState(ERHIAccess::UAVCompute);
			X->Tex = RHICmdList.CreateTexture(Desc);
		}
		{
			FRDGBuilder GraphBuilder(RHICmdList);
			FRDGTextureRef In = RegisterExternalTexture(GraphBuilder, Src, TEXT("RogueLinkIn"));
			FRDGTextureRef Out = RegisterExternalTexture(GraphBuilder, X->Tex, TEXT("RogueLinkOut"));
			FRogueLinkAlphaCS::FParameters* P = GraphBuilder.AllocParameters<FRogueLinkAlphaCS::FParameters>();
			P->InTex = In;
			P->OutTex = GraphBuilder.CreateUAV(Out);
			P->Size = FUintVector2(Size.X, Size.Y);
			P->Encode = EnumHasAnyFlags(Src->GetFlags(), ETextureCreateFlags::SRGB) ? 1u : 0u;
			TShaderMapRef<FRogueLinkAlphaCS> CS(GetGlobalShaderMap(GMaxRHIFeatureLevel));
			FComputeShaderUtils::AddPass(GraphBuilder, RDG_EVENT_NAME("RogueLinkAlpha"), CS, P, FComputeShaderUtils::GetGroupCount(Size, 8));
			GraphBuilder.SetTextureAccessFinal(Out, ERHIAccess::CopySrc);
			GraphBuilder.Execute();
		}
		FTextureRHIRef OutTex = X->Tex;
		RHICmdList.EnqueueLambda(TEXT("RogueLinkCopy"), [L, OutTex, F](auto&)
		{
			L->CopyToRing(OutTex->GetNativeResource(), F);
		});
	});
}
