// UnrealLinkNative: the game-side D3D11 helper of the UnrealLink plugin (source/UnrealLink).
//
// Unreal (RogueLink plugin, run with -dx11) renders the avatar into a ring of shared textures, each created with
// D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX and published as a legacy shared handle in the memory-mapped file
// Local\RogueUnrealLink.State. Both sides use the keyed mutex as a plain lock on key 0 (acquire 0 / release 0), so a
// slot the game skipped never blocks Unreal. This DLL:
//   - takes Unity's ID3D11Device from any Unity texture (ID3D11DeviceChild::GetDevice); the device is free-threaded,
//     so opening the shared textures happens on the main thread;
//   - copies the newest slot into a Unity RenderTexture on Unity's render thread (GL.IssuePluginEvent with the slot
//     as the event id): AcquireSync(0, 0 ms) -> CopyResource -> ReleaseSync(0). A busy slot is skipped (the last
//     picture stays). Only the immediate context's copy and the keyed mutex are used: no pipeline state is touched;
//   - in standalone mode (test tool) makes its own device and reads a slot back into memory, and times the copy on
//     the GPU with timestamp queries.
// Built by Native\build.cmd (cl /LD). Plain C exports, no Unity plugin interface (it is loaded with LoadLibrary).

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define UL_API extern "C" __declspec(dllexport)

static const int MaxSlots = 3;
static const int Version = 1;

struct Slots
{
    ID3D11Texture2D* tex[MaxSlots] = {};
    IDXGIKeyedMutex* mutex[MaxSlots] = {};
    uint64_t handle[MaxSlots] = {};
    int count = 0, w = 0, h = 0;
    DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN;

    void Release()
    {
        for (int i = 0; i < MaxSlots; i++)
        {
            if (mutex[i]) mutex[i]->Release();
            if (tex[i]) tex[i]->Release();
            mutex[i] = nullptr; tex[i] = nullptr; handle[i] = 0;
        }
        count = w = h = 0; fmt = DXGI_FORMAT_UNKNOWN;
    }
};

static SRWLOCK g_lock = SRWLOCK_INIT;
static ID3D11Device* g_dev = nullptr;
static ID3D11DeviceContext* g_ownCtx = nullptr;   // standalone mode only
static bool g_standalone = false;
static Slots g_slots;
static ID3D11Texture2D* g_target = nullptr;       // Unity's RenderTexture (AddRef'd)
static char g_err[512] = "";

// stats (render thread writes, main thread reads; plain ints are fine for a status line)
static volatile LONG g_events = 0, g_copies = 0, g_busy = 0, g_fails = 0, g_mismatch = 0;
static volatile LONG g_lastSlot = -1;
static double g_copyUsSum = 0, g_copyUsMax = 0;   // CPU time spent in the render event (submit cost, not GPU time)
static LONG g_copyUsN = 0;
static LARGE_INTEGER g_qpf;

static void SetErr(const char* what, HRESULT hr)
{
    _snprintf_s(g_err, sizeof(g_err), _TRUNCATE, "%s (hr 0x%08X)", what, (unsigned)hr);
}

static DXGI_FORMAT Typeless(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        return DXGI_FORMAT_R8G8B8A8_TYPELESS;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return DXGI_FORMAT_B8G8R8A8_TYPELESS;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: case DXGI_FORMAT_R16G16B16A16_FLOAT:
        return DXGI_FORMAT_R16G16B16A16_TYPELESS;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: case DXGI_FORMAT_R10G10B10A2_UNORM:
        return DXGI_FORMAT_R10G10B10A2_TYPELESS;
    default: return f;
    }
}

// ------------------------------------------------------------------------------------------------ setup

/// Unity mode: the device comes from any Unity texture (GetNativeTexturePtr: an ID3D11Resource). Returns 1 on success.
UL_API int UL_Init(void* unityTexture)
{
    QueryPerformanceFrequency(&g_qpf);
    if (g_dev) return 1;
    if (!unityTexture) { SetErr("UL_Init: no texture", E_POINTER); return 0; }
    ID3D11Device* dev = nullptr;
    ((ID3D11DeviceChild*)unityTexture)->GetDevice(&dev);
    if (!dev) { SetErr("UL_Init: GetDevice returned null", E_FAIL); return 0; }
    g_dev = dev;   // GetDevice AddRef'd it
    g_standalone = false;
    return 1;
}

/// Test-tool mode: our own hardware device. Returns 1 on success.
UL_API int UL_InitStandalone()
{
    QueryPerformanceFrequency(&g_qpf);
    if (g_dev) return 1;
    D3D_FEATURE_LEVEL fl[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   fl, 2, D3D11_SDK_VERSION, &g_dev, nullptr, &g_ownCtx);
    if (FAILED(hr)) { SetErr("D3D11CreateDevice", hr); g_dev = nullptr; return 0; }
    g_standalone = true;
    return 1;
}

UL_API int UL_Version() { return Version; }

UL_API const char* UL_LastError() { return g_err; }

/// Opens Unreal's shared ring (count handles). Returns 1 = opened, 0 = failed (UL_LastError). Main thread.
UL_API int UL_OpenShared(const uint64_t* handles, int count, int w, int h, int dxgiFormat)
{
    if (!g_dev) { SetErr("UL_OpenShared: not initialised", E_FAIL); return 0; }
    if (count < 1 || count > MaxSlots || !handles) { SetErr("UL_OpenShared: bad slot count", E_INVALIDARG); return 0; }
    Slots s;
    s.count = count; s.w = w; s.h = h; s.fmt = (DXGI_FORMAT)dxgiFormat;
    for (int i = 0; i < count; i++)
    {
        HRESULT hr = g_dev->OpenSharedResource((HANDLE)(uintptr_t)handles[i], __uuidof(ID3D11Texture2D), (void**)&s.tex[i]);
        if (FAILED(hr) || !s.tex[i]) { SetErr("OpenSharedResource", hr); s.Release(); return 0; }
        hr = s.tex[i]->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&s.mutex[i]);
        if (FAILED(hr) || !s.mutex[i]) { SetErr("no keyed mutex on the shared texture", hr); s.Release(); return 0; }
        D3D11_TEXTURE2D_DESC d; s.tex[i]->GetDesc(&d);
        if ((int)d.Width != w || (int)d.Height != h) { SetErr("shared texture size differs from the published size", E_FAIL); s.Release(); return 0; }
        s.fmt = d.Format;
        s.handle[i] = handles[i];
    }
    AcquireSRWLockExclusive(&g_lock);
    g_slots.Release();
    g_slots = s;   // takes the references
    ReleaseSRWLockExclusive(&g_lock);
    g_err[0] = 0;
    return 1;
}

/// The texture the render event copies into (a Unity RenderTexture's GetNativeTexturePtr, same size and format
/// family as the shared ring), or null to stop copying. Returns 1 = compatible, 0 = refused (UL_LastError).
UL_API int UL_SetTarget(void* nativeTexture)
{
    ID3D11Texture2D* t = (ID3D11Texture2D*)nativeTexture;
    if (t)
    {
        D3D11_TEXTURE2D_DESC d; t->GetDesc(&d);
        AcquireSRWLockShared(&g_lock);
        int w = g_slots.w, h = g_slots.h; DXGI_FORMAT f = g_slots.fmt;
        ReleaseSRWLockShared(&g_lock);
        if (w > 0 && ((int)d.Width != w || (int)d.Height != h))
        { SetErr("target size differs from the shared texture", E_INVALIDARG); return 0; }
        if (f != DXGI_FORMAT_UNKNOWN && Typeless(d.Format) != Typeless(f))
        {
            _snprintf_s(g_err, sizeof(g_err), _TRUNCATE, "target format %d is not in the shared format %d's family", (int)d.Format, (int)f);
            return 0;
        }
        if (d.SampleDesc.Count != 1 || d.MipLevels != 1) { SetErr("target must have 1 mip and no MSAA", E_INVALIDARG); return 0; }
        t->AddRef();
    }
    AcquireSRWLockExclusive(&g_lock);
    if (g_target) g_target->Release();
    g_target = t;
    ReleaseSRWLockExclusive(&g_lock);
    return 1;
}

// ------------------------------------------------------------------------------------------------ the render event

static void __stdcall OnRenderEvent(int eventId)
{
    InterlockedIncrement(&g_events);
    int slot = eventId & 0xFF;
    LARGE_INTEGER t0; QueryPerformanceCounter(&t0);
    AcquireSRWLockExclusive(&g_lock);
    if (g_dev && g_target && slot >= 0 && slot < g_slots.count && g_slots.mutex[slot])
    {
        ID3D11DeviceContext* ctx = nullptr;
        g_dev->GetImmediateContext(&ctx);
        if (ctx)
        {
            HRESULT hr = g_slots.mutex[slot]->AcquireSync(0, 0);
            if (hr == S_OK || hr == (HRESULT)WAIT_ABANDONED)
            {
                ctx->CopyResource(g_target, g_slots.tex[slot]);
                g_slots.mutex[slot]->ReleaseSync(0);
                InterlockedIncrement(&g_copies);
                g_lastSlot = slot;
            }
            else if (hr == (HRESULT)WAIT_TIMEOUT) InterlockedIncrement(&g_busy);
            else InterlockedIncrement(&g_fails);
            ctx->Release();
        }
    }
    else if (slot >= g_slots.count) InterlockedIncrement(&g_mismatch);
    ReleaseSRWLockExclusive(&g_lock);
    LARGE_INTEGER t1; QueryPerformanceCounter(&t1);
    double us = (double)(t1.QuadPart - t0.QuadPart) * 1e6 / (double)g_qpf.QuadPart;
    g_copyUsSum += us; g_copyUsN++;
    if (us > g_copyUsMax) g_copyUsMax = us;
}

/// For GL.IssuePluginEvent(func, slot).
UL_API void* UL_GetRenderEventFunc() { return (void*)&OnRenderEvent; }

/// Counters since the last call (events, copies, busy skips, failures); the CPU cost of the render event in
/// microseconds (average and max over the same window). Resets the window.
UL_API void UL_GetStats(int* events, int* copies, int* busy, int* fails, double* avgUs, double* maxUs)
{
    if (events) *events = (int)InterlockedExchange(&g_events, 0);
    if (copies) *copies = (int)InterlockedExchange(&g_copies, 0);
    if (busy) *busy = (int)InterlockedExchange(&g_busy, 0);
    if (fails) *fails = (int)InterlockedExchange(&g_fails, 0) + (int)InterlockedExchange(&g_mismatch, 0);
    LONG n = g_copyUsN;
    if (avgUs) *avgUs = n > 0 ? g_copyUsSum / n : 0;
    if (maxUs) *maxUs = g_copyUsMax;
    g_copyUsSum = 0; g_copyUsN = 0; g_copyUsMax = 0;
}

/// Releases everything (shared textures, target, device). Safe to call twice.
UL_API void UL_Close()
{
    AcquireSRWLockExclusive(&g_lock);
    g_slots.Release();
    if (g_target) { g_target->Release(); g_target = nullptr; }
    if (g_ownCtx) { g_ownCtx->Release(); g_ownCtx = nullptr; }
    if (g_dev) { g_dev->Release(); g_dev = nullptr; }
    ReleaseSRWLockExclusive(&g_lock);
}

// ------------------------------------------------------------------------------------------------ test tool only

/// Standalone mode: copies one slot into a staging texture and writes its rows (4 bytes a pixel, the shared
/// texture's own channel order) into dst (w*h*4 bytes). timeoutMs for the keyed mutex. Returns 1 = ok.
UL_API int UL_ReadSlot(int slot, uint8_t* dst, int dstBytes, int timeoutMs)
{
    if (!g_standalone || !g_ownCtx) { SetErr("UL_ReadSlot: standalone mode only", E_FAIL); return 0; }
    if (slot < 0 || slot >= g_slots.count) { SetErr("UL_ReadSlot: bad slot", E_INVALIDARG); return 0; }
    int w = g_slots.w, h = g_slots.h;
    if (dstBytes < w * h * 4) { SetErr("UL_ReadSlot: buffer too small", E_INVALIDARG); return 0; }
    D3D11_TEXTURE2D_DESC d; g_slots.tex[slot]->GetDesc(&d);
    d.Usage = D3D11_USAGE_STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ; d.MiscFlags = 0;
    ID3D11Texture2D* st = nullptr;
    HRESULT hr = g_dev->CreateTexture2D(&d, nullptr, &st);
    if (FAILED(hr)) { SetErr("CreateTexture2D staging", hr); return 0; }
    hr = g_slots.mutex[slot]->AcquireSync(0, (DWORD)timeoutMs);
    if (hr != S_OK && hr != (HRESULT)WAIT_ABANDONED) { st->Release(); SetErr("AcquireSync", hr); return 0; }
    g_ownCtx->CopyResource(st, g_slots.tex[slot]);
    g_slots.mutex[slot]->ReleaseSync(0);
    D3D11_MAPPED_SUBRESOURCE m;
    hr = g_ownCtx->Map(st, 0, D3D11_MAP_READ, 0, &m);
    if (FAILED(hr)) { st->Release(); SetErr("Map", hr); return 0; }
    for (int y = 0; y < h; y++) memcpy(dst + (size_t)y * w * 4, (uint8_t*)m.pData + (size_t)y * m.RowPitch, (size_t)w * 4);
    g_ownCtx->Unmap(st, 0);
    st->Release();
    return 1;
}

/// Standalone mode: the game-side copy (keyed mutex + CopyResource into a private texture) repeated n times, timed on
/// the GPU with timestamp queries. Returns the average GPU milliseconds per copy, or a negative number on failure.
UL_API double UL_TimeCopyGpu(int slot, int n)
{
    if (!g_standalone || !g_ownCtx || slot < 0 || slot >= g_slots.count || n < 1) return -1;
    D3D11_TEXTURE2D_DESC d; g_slots.tex[slot]->GetDesc(&d);
    d.MiscFlags = 0; d.BindFlags = D3D11_BIND_SHADER_RESOURCE; d.Format = Typeless(d.Format);
    ID3D11Texture2D* dst = nullptr;
    if (FAILED(g_dev->CreateTexture2D(&d, nullptr, &dst))) return -2;
    D3D11_QUERY_DESC qd = { D3D11_QUERY_TIMESTAMP_DISJOINT, 0 };
    ID3D11Query *dis = nullptr, *q0 = nullptr, *q1 = nullptr;
    g_dev->CreateQuery(&qd, &dis);
    qd.Query = D3D11_QUERY_TIMESTAMP;
    g_dev->CreateQuery(&qd, &q0); g_dev->CreateQuery(&qd, &q1);
    double ms = -3;
    if (dis && q0 && q1)
    {
        g_ownCtx->Begin(dis);
        g_ownCtx->End(q0);
        int done = 0;
        for (int i = 0; i < n; i++)
        {
            HRESULT hr = g_slots.mutex[slot]->AcquireSync(0, 100);
            if (hr != S_OK && hr != (HRESULT)WAIT_ABANDONED) continue;
            g_ownCtx->CopyResource(dst, g_slots.tex[slot]);
            g_slots.mutex[slot]->ReleaseSync(0);
            done++;
        }
        g_ownCtx->End(q1);
        g_ownCtx->End(dis);
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dd = {};
        while (g_ownCtx->GetData(dis, &dd, sizeof(dd), 0) == S_FALSE) Sleep(1);
        UINT64 t0 = 0, t1 = 0;
        while (g_ownCtx->GetData(q0, &t0, sizeof(t0), 0) == S_FALSE) Sleep(1);
        while (g_ownCtx->GetData(q1, &t1, sizeof(t1), 0) == S_FALSE) Sleep(1);
        if (!dd.Disjoint && dd.Frequency > 0 && done > 0) ms = (double)(t1 - t0) * 1000.0 / (double)dd.Frequency / done;
        else ms = -4;
    }
    if (dis) dis->Release(); if (q0) q0->Release(); if (q1) q1->Release();
    dst->Release();
    return ms;
}
