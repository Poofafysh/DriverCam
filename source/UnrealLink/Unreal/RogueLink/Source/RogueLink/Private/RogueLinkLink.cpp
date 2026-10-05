#include "RogueLinkLink.h"

#include "Windows/AllowWindowsPlatformTypes.h"
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include "Windows/HideWindowsPlatformTypes.h"

DEFINE_LOG_CATEGORY(LogRogueLink);

using namespace RogueLink;

FRogueLinkLink::~FRogueLinkLink()
{
	Close();
}

bool FRogueLinkLink::Open(FString& OutError)
{
	if (View) return true;
	HANDLE M = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, MapSize, MapName);
	if (!M) { OutError = FString::Printf(TEXT("CreateFileMapping failed (%u)"), GetLastError()); return false; }
	void* V = MapViewOfFile(M, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, MapSize);
	if (!V) { OutError = FString::Printf(TEXT("MapViewOfFile failed (%u)"), GetLastError()); CloseHandle(M); return false; }
	HANDLE E = CreateEventW(nullptr, 0, 0, EventName);
	if (!E) { OutError = FString::Printf(TEXT("CreateEvent failed (%u)"), GetLastError()); UnmapViewOfFile(V); CloseHandle(M); return false; }
	Map = M; View = (uint8*)V; Event = E;
	// generations start from this process's id (high bits), so a restarted Unreal never republishes a generation the
	// game already opened from the previous (crashed / closed) process; CreateRing adds 1 per ring
	HandleGen = (uint32)GetCurrentProcessId() << 12;
	WriteUe([](FUeBlock& U)
	{
		U.Version = Version;
		U.Pid = GetCurrentProcessId();
		U.Status = S_Starting;
		U.ReadySlot = -1;
		U.Slots = 0;
		for (int i = 0; i < MaxSlots; i++) { U.Handles[i] = 0; U.SlotFrame[i] = 0; }
	});
	return true;
}

void FRogueLinkLink::Close()
{
	if (View)
	{
		WriteUe([](FUeBlock& U) { U.Status = S_Starting; U.ReadySlot = -1; U.Slots = 0; U.Pid = 0; });
	}
	ReleaseRing();
	if (View) { UnmapViewOfFile(View); View = nullptr; }
	if (Map) { CloseHandle((HANDLE)Map); Map = nullptr; }
	if (Event) { CloseHandle((HANDLE)Event); Event = nullptr; }
}

bool FRogueLinkLink::WaitFrame(uint32 TimeoutMs)
{
	return Event && WaitForSingleObject((HANDLE)Event, TimeoutMs) == WAIT_OBJECT_0;
}

bool FRogueLinkLink::ReadGame(FGameBlock& Out) const
{
	if (!View) return false;
	volatile uint32* Seq = (volatile uint32*)(View + GameBlockOffset + offsetof(FGameBlock, Seq));
	for (int Try = 0; Try < 50; Try++)
	{
		uint32 A = *Seq;
		if (A & 1u) { FPlatformProcess::YieldThread(); continue; }
		FPlatformMisc::MemoryBarrier();
		FMemory::Memcpy(&Out, View + GameBlockOffset, sizeof(FGameBlock));
		FPlatformMisc::MemoryBarrier();
		uint32 B = *Seq;
		if (A == B) return Out.Magic == RogueLink::Magic && Out.Version == RogueLink::Version;
	}
	return false;
}

void FRogueLinkLink::WriteUe(TFunctionRef<void(FUeBlock&)> Edit)
{
	if (!View) return;
	FScopeLock Lock(&UeLock);
	FUeBlock* U = (FUeBlock*)(View + UeBlockOffset);
	volatile uint32* Seq = (volatile uint32*)&U->Seq;
	uint32 S = *Seq;
	*Seq = (S & 1u) ? S + 2u : S + 1u;   // odd: writing
	FPlatformMisc::MemoryBarrier();
	Edit(*U);
	FPlatformMisc::MemoryBarrier();
	S = *Seq;
	*Seq = (S & 1u) ? S + 1u : S + 2u;   // even: done
}

void FRogueLinkLink::Heartbeat(uint32 UeFrame, float FrameMs, float GpuMs, float WaitMs, uint32 LastGameFrame)
{
	WriteUe([&](FUeBlock& U)
	{
		U.Frame = UeFrame;
		U.FrameMs = FrameMs;
		U.GpuMs = GpuMs;
		U.WaitMs = WaitMs;
		U.LastGameFrame = LastGameFrame;
	});
}

FString FRogueLinkLink::TakeRenderError()
{
	FScopeLock Lock(&ErrLock);
	FString E = MoveTemp(RenderError);
	RenderError.Reset();
	return E;
}

void FRogueLinkLink::ReleaseRing()
{
	for (int i = 0; i < MaxSlots; i++)
	{
		if (RingMutex[i]) ((IDXGIKeyedMutex*)RingMutex[i])->Release();
		if (RingTex[i]) ((ID3D11Texture2D*)RingTex[i])->Release();
		RingMutex[i] = RingTex[i] = nullptr;
		RingHandle[i] = 0;
	}
	RingW = RingH = 0;
	RingFormat = 0;
	ReadySlot = -1;
}

static DXGI_FORMAT TypedFormat(DXGI_FORMAT F)
{
	switch (F)
	{
	case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
	case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
	case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
	case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
	default: return F;
	}
}

bool FRogueLinkLink::CreateRing(void* DevicePtr, int32 W, int32 H, uint32 Format)
{
	ReleaseRing();
	ID3D11Device* Device = (ID3D11Device*)DevicePtr;
	D3D11_TEXTURE2D_DESC D = {};
	D.Width = W; D.Height = H; D.MipLevels = 1; D.ArraySize = 1;
	D.Format = TypedFormat((DXGI_FORMAT)Format);
	D.SampleDesc.Count = 1;
	D.Usage = D3D11_USAGE_DEFAULT;
	D.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
	D.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
	for (int i = 0; i < MaxSlots; i++)
	{
		ID3D11Texture2D* T = nullptr;
		HRESULT Hr = Device->CreateTexture2D(&D, nullptr, &T);
		if (FAILED(Hr) || !T)
		{
			FScopeLock Lock(&ErrLock);
			RenderError = FString::Printf(TEXT("CreateTexture2D (shared, %dx%d, format %u) failed: 0x%08X"), W, H, (uint32)D.Format, (uint32)Hr);
			ReleaseRing();
			return false;
		}
		RingTex[i] = T;
		IDXGIKeyedMutex* KM = nullptr;
		T->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&KM);
		RingMutex[i] = KM;
		IDXGIResource* R = nullptr;
		HANDLE Hd = nullptr;
		if (SUCCEEDED(T->QueryInterface(__uuidof(IDXGIResource), (void**)&R)) && R)
		{
			R->GetSharedHandle(&Hd);
			R->Release();
		}
		if (!KM || !Hd)
		{
			FScopeLock Lock(&ErrLock);
			RenderError = TEXT("shared texture has no keyed mutex or shared handle");
			ReleaseRing();
			return false;
		}
		RingHandle[i] = (uint64)(UPTRINT)Hd;
	}
	RingW = W; RingH = H; RingFormat = (uint32)D.Format;
	if (++HandleGen == 0) HandleGen = 1;   // 0 = "none" on the game side
	const uint32 Gen = HandleGen;
	WriteUe([&](FUeBlock& U)
	{
		U.W = W; U.H = H; U.Format = (uint32)D.Format; U.Slots = MaxSlots;
		for (int i = 0; i < MaxSlots; i++) { U.Handles[i] = RingHandle[i]; U.SlotFrame[i] = 0; }
		U.ReadySlot = -1;
		U.HandleGen = Gen;
		U.Status = S_Ready;
	});
	UE_LOG(LogRogueLink, Log, TEXT("shared texture ring: %d x %d, DXGI format %u, %d slots (handle generation %u)"), W, H, (uint32)D.Format, MaxSlots, Gen);
	return true;
}

void FRogueLinkLink::CopyToRing(void* NativeSourceTexture, uint32 GameFrame)
{
	if (!View || !NativeSourceTexture) return;
	ID3D11Texture2D* Src = (ID3D11Texture2D*)NativeSourceTexture;
	ID3D11Device* Device = nullptr;
	Src->GetDevice(&Device);
	if (!Device) return;
	D3D11_TEXTURE2D_DESC SD;
	Src->GetDesc(&SD);
	if (!RingTex[0] || (int32)SD.Width != RingW || (int32)SD.Height != RingH || TypedFormat(SD.Format) != (DXGI_FORMAT)RingFormat)
	{
		if (!CreateRing(Device, SD.Width, SD.Height, SD.Format))
		{
			WriteUe([](FUeBlock& U) { U.Status = S_Error; });
			Device->Release();
			return;
		}
	}
	ID3D11DeviceContext* Ctx = nullptr;
	Device->GetImmediateContext(&Ctx);
	int32 Slot = -1;
	for (int k = 1; k <= MaxSlots; k++)
	{
		int32 S = (ReadySlot + k + MaxSlots) % MaxSlots;
		// 0 ms first; the last try waits up to 2 ms (the game holds a slot only for one CopyResource)
		HRESULT Hr = ((IDXGIKeyedMutex*)RingMutex[S])->AcquireSync(0, k == MaxSlots ? 2 : 0);
		if (Hr == S_OK || Hr == (HRESULT)WAIT_ABANDONED) { Slot = S; break; }
	}
	if (Slot >= 0 && Ctx)
	{
		Ctx->CopyResource((ID3D11Texture2D*)RingTex[Slot], Src);
		((IDXGIKeyedMutex*)RingMutex[Slot])->ReleaseSync(0);
		ReadySlot = Slot;
		const float Work = (float)((FPlatformTime::Seconds() - WakeSeconds) * 1000.0);
		WriteUe([&](FUeBlock& U)
		{
			U.SlotFrame[Slot] = GameFrame;
			U.ReadySlot = Slot;
			U.WorkMs = Work;
		});
	}
	if (Ctx) Ctx->Release();
	Device->Release();
}
