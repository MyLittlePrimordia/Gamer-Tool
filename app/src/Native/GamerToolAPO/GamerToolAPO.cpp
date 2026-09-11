/*
    GamerToolAPO - implementation. See GamerToolAPO.h for the architecture
    overview. RT-safety rules are absolute for everything under the
    #pragma AVRT_CODE markers.
*/

#include "stdafx.h"
// initguid.h makes the DEFINE_GUID(CLSID_GamerToolAPO, ...) in GamerToolAPO.h
// allocate storage in exactly this translation unit (without it the GUID is
// a mere extern declaration and the link fails with LNK2001).
#include <initguid.h>
#include "GamerToolAPO.h"
#include "ClassFactory.h"
#include <Unknwn.h>
#include <math.h>
#include <stdio.h>
#include <stdarg.h>

#define GAMERTOOL_SHARED_MEMORY_NAME L"Local\\GamerToolEqConfig"

// ---------------------------------------------------------------------------
// audiodg-side diagnostics. The engine gives NO feedback when it skips an
// APO, so these append-only lines (Initialize + format negotiation + lock
// only - NEVER the real-time APOProcess path) are how a failed load is
// distinguished from a config/DSP problem. %ProgramData% is Everyone-writable
// so this works under audiodg's service account too.
// ---------------------------------------------------------------------------
static void ApoLog(const wchar_t* fmt, ...)
{
    wchar_t dir[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"ProgramData", dir, MAX_PATH);
    if (n == 0 || n + 32 >= MAX_PATH)
        return;

    wchar_t path[MAX_PATH];
    _snwprintf_s(path, MAX_PATH, _TRUNCATE, L"%s\\GamerTool\\apo-load.log", dir);

    FILE* f = NULL;
    if (_wfopen_s(&f, path, L"a, ccs=UTF-16LE") != 0 || f == NULL)
        return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    fwprintf(f, L"[%02u:%02u:%02u.%03u] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    va_list args;
    va_start(args, fmt);
    vfwprintf(f, fmt, args);
    va_end(args);
    fwprintf(f, L"\n");
    fclose(f);
}

// ---------------------------------------------------------------------------
// Registration metadata - CRegAPOProperties<1> declares one APO CLSID.
// APO_FLAG_INPLACE lets us process directly into the output buffer when the
// engine gives us that luxury (both connection buffers may alias).
//
// CRegAPOProperties ends in iidAPOInterfaceList[1] - a flexible-array-style
// tail the audio SDK uses everywhere. MSVC /W4 flags that shape on the
// aggregate initializer ("zero-sized array...will have no elements",
// emitted for the trailing member) even though the layout is exactly what
// GetRegistrationProperties consumers expect. Verified against in-box APOs:
// the byte layout below matches their registry blobs, so the warning is
// informational only and is disabled for this one declaration.
// ---------------------------------------------------------------------------
#pragma warning(push)
#pragma warning(disable: 4200) // nonstandard extension: zero-sized array in struct
const CRegAPOProperties<1> GamerToolAPO::regProperties(
    CLSID_GamerToolAPO, L"GamerToolAPO", L"Gamer Tool built-in equalizer", 1, 0,
    __uuidof(IAudioProcessingObject),
    (APO_FLAG)(APO_FLAG_FRAMESPERSECOND_MUST_MATCH | APO_FLAG_BITSPERSAMPLE_MUST_MATCH | APO_FLAG_INPLACE));
#pragma warning(pop)

// ---------------------------------------------------------------------------
// Object creation (ClassFactory.cpp entry point).
// ---------------------------------------------------------------------------
HRESULT __stdcall CreateGamerToolAPO(IUnknown* pUnkOuter, IUnknown** ppOut)
{
    if (ppOut == NULL)
        return E_POINTER;
    *ppOut = NULL;
    if (pUnkOuter != NULL)
        return CLASS_E_NOAGGREGATION;
    GamerToolAPO* obj = new (std::nothrow) GamerToolAPO();
    if (obj == NULL)
        return E_OUTOFMEMORY;
    // Ctor refs at 1; hand that reference to the caller, cast via the single
    // unambiguous IAudioProcessingObject path.
    *ppOut = static_cast<IAudioProcessingObject*>(obj);
    return S_OK;
}

GamerToolAPO::GamerToolAPO()
{
    m_refCount = 1;
    m_initialized = false;
    m_locked = false;

    mappingHandle = NULL;
    sharedConfig = NULL;
    channelCount = 0;
    sampleRate = 0;
    appliedVersion = 0;
    appliedEnabled = 0;
    ZeroMemory(appliedGains, sizeof(appliedGains));
    ResetFilters();
}

GamerToolAPO::~GamerToolAPO()
{
    if (sharedConfig != NULL)
        UnmapViewOfFile(sharedConfig);
    if (mappingHandle != NULL)
        CloseHandle(mappingHandle);
}

// ---------------------------------------------------------------------------
// IUnknown - plain refcount; every branch casts through exactly one base
// path, so no ambiguity arises from the double IUnknown inheritance.
// ---------------------------------------------------------------------------
HRESULT __stdcall GamerToolAPO::QueryInterface(const IID& iid, void** ppv)
{
    if (ppv == NULL)
        return E_POINTER;
    *ppv = NULL;

    if (iid == __uuidof(IUnknown))
        *ppv = static_cast<IAudioProcessingObject*>(this);
    else if (iid == __uuidof(IAudioProcessingObject))
        *ppv = static_cast<IAudioProcessingObject*>(this);
    else if (iid == __uuidof(IAudioProcessingObjectRT))
        *ppv = static_cast<IAudioProcessingObjectRT*>(this);
    else if (iid == __uuidof(IAudioProcessingObjectConfiguration))
        *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
    else if (iid == __uuidof(IAudioSystemEffects))
        *ppv = static_cast<IAudioSystemEffects*>(this);
    else
        return E_NOINTERFACE;

    AddRef();
    return S_OK;
}

ULONG __stdcall GamerToolAPO::AddRef()
{
    return (ULONG)InterlockedIncrement(&m_refCount);
}

ULONG __stdcall GamerToolAPO::Release()
{
    LONG remaining = InterlockedDecrement(&m_refCount);
    if (remaining == 0)
        delete this;
    return (ULONG)remaining;
}

// ---------------------------------------------------------------------------
// Config plumbing (non-RT)
// ---------------------------------------------------------------------------
HANDLE GamerToolAPO::OpenSharedConfig()
{
    // Create-or-open: the first APO instance in audiodg creates the section;
    // GamerTool.exe opens the same name when applying presets. audiodg runs
    // this APO under a different account (LocalService) than the desktop
    // app, so passing NULL here (the "default" security descriptor, tied to
    // the creator's own account) would let whichever side gets here first
    // silently lock the other one out - the APO would then never see a
    // real config and just pass audio straight through untouched. A truly
    // NULL DACL (built explicitly below, not just a NULL parameter) grants
    // access to any account regardless of creation order.
    SECURITY_DESCRIPTOR sd;
    InitializeSecurityDescriptor(&sd, SECURITY_DESCRIPTOR_REVISION);
    SetSecurityDescriptorDacl(&sd, TRUE, NULL, FALSE);

    SECURITY_ATTRIBUTES sa;
    sa.nLength = sizeof(sa);
    sa.lpSecurityDescriptor = &sd;
    sa.bInheritHandle = FALSE;

    HANDLE h = CreateFileMappingW(
        INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE,
        0, sizeof(EqConfig), GAMERTOOL_SHARED_MEMORY_NAME);
    if (h != NULL && GetLastError() == ERROR_ALREADY_EXISTS)
    {
        // opened existing - fine
    }
    return h;
}

HRESULT __stdcall GamerToolAPO::Initialize(UINT32 cbDataSize, BYTE* pbyData)
{
    if ((NULL == pbyData) && (0 != cbDataSize))
        return E_INVALIDARG;
    if ((NULL != pbyData) && (0 == cbDataSize))
        return E_POINTER;

    ApoLog(L"Initialize: blob %u bytes (Effects=%u, Effects2=%u)",
        cbDataSize, (UINT32)sizeof(APOInitSystemEffects), (UINT32)sizeof(APOInitSystemEffects2));

    // Accept both the classic APOInitSystemEffects and the Windows 10+
    // APOInitSystemEffects2 (which carries the device GUID we don't need -
    // all devices share the one Gamer Tool config).
    if (cbDataSize == sizeof(APOInitSystemEffects) || cbDataSize == sizeof(APOInitSystemEffects2))
    {
        m_initialized = true;
    }
    else
    {
        // Fallback for any other init blob shape (e.g. APOInitSystemEffects3
        // on Win11 22H2+ driver stacks): accept and keep the graph alive.
        m_initialized = true;
    }

    // Open (or create) the shared config on EVERY successful init path.
    // The original opened it only for the two known blob sizes, so a
    // system that passes Effects3 initialized the APO into a silent
    // permanent bypass: process runs, sharedConfig stays NULL, the EQ
    // never sees any config and audio passes through untouched - the
    // "enabled but does nothing" failure mode.
    if (m_initialized && sharedConfig == NULL)
    {
        mappingHandle = OpenSharedConfig();
        if (mappingHandle != NULL)
        {
            sharedConfig = (EqConfig*)MapViewOfFile(
                mappingHandle, FILE_MAP_READ, 0, 0, sizeof(EqConfig));
        }
        ApoLog(L"Initialize: mapping=%s view=%s",
            mappingHandle != NULL ? L"ok" : L"FAILED",
            sharedConfig != NULL ? L"ok" : L"FAILED");
    }

    return S_OK;
}

HRESULT __stdcall GamerToolAPO::GetLatency(HNSTIME* pTime)
{
    if (!pTime)
        return E_POINTER;
    // Unconditional: some graphs query latency before LockForProcess, and a
    // failure there can abort graph building. Biquads are pure feedforward
    // state and add no latency either way.
    *pTime = 0;
    return S_OK;
}

HRESULT __stdcall GamerToolAPO::GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps)
{
    if (ppRegProps == NULL)
        return E_POINTER;
    *ppRegProps = NULL;

    // Single-interface registration: the struct ends after iidAPOInterfaceList[0].
    const APO_REG_PROPERTIES* src = regProperties;
    APO_REG_PROPERTIES* copy = (APO_REG_PROPERTIES*)CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES));
    if (copy == NULL)
        return E_OUTOFMEMORY;
    memcpy(copy, src, sizeof(APO_REG_PROPERTIES));
    *ppRegProps = copy;
    return S_OK;
}

// Reads an IAudioMediaType as float32 stream parameters. S_OK + filled
// out-params on success; APOERR_FORMAT_NOT_SUPPORTED for anything we cannot
// process (compressed types, non-float, empty channel count/rate).
static HRESULT GetFloatParams(IAudioMediaType* pType, UINT32* pChannels, UINT32* pRateHz)
{
    if (pType == NULL)
        return E_POINTER;

    UNCOMPRESSEDAUDIOFORMAT fmt;
    ZeroMemory(&fmt, sizeof(fmt));
    HRESULT hr = pType->GetUncompressedAudioFormat(&fmt);
    if (FAILED(hr))
        return APOERR_FORMAT_NOT_SUPPORTED;

    if (fmt.guidFormatType != KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
        return APOERR_FORMAT_NOT_SUPPORTED;

    if (pChannels != NULL)
        *pChannels = fmt.dwSamplesPerFrame;
    if (pRateHz != NULL)
        *pRateHz = (UINT32)fmt.fFramesPerSecond;
    return S_OK;
}

// Accept only 32-bit float, and - unlike the original version - only when it
// is COMPATIBLE WITH THE OTHER SIDE of the connection (same channels, same
// rate). The engine probes mismatched pairs during graph building and
// expects rejection; blindly accepting any float32 input let an impossible
// pair through to LockForProcess, whose failure then counted toward the
// engine's per-APO failure budget (10 strikes disables the endpoint's SysFx
// outright - exactly the silent death observed on 24H2).
//
// Contract, per MSDN: S_OK means "use the requested format" with
// *ppSupported... set to NULL. (The old code AddRef'd the request into the
// out-param on success, which the engine is free to misread.)
static HRESULT CheckFloatPair(IAudioMediaType* pCounterpartFormat,
    IAudioMediaType* pRequestedFormat, IAudioMediaType** ppSupportedFormat,
    const wchar_t* tag)
{
    if (pRequestedFormat == NULL || ppSupportedFormat == NULL)
        return E_POINTER;
    *ppSupportedFormat = NULL;

    UINT32 reqCh = 0, reqRate = 0;
    HRESULT hr = GetFloatParams(pRequestedFormat, &reqCh, &reqRate);
    if (FAILED(hr) || reqCh == 0 || reqCh > (UINT32)GamerToolAPO::MaxChannels || reqRate == 0)
    {
        ApoLog(L"%ls reject: not usable float (hr=0x%08X ch=%u rate=%u)", tag, hr, reqCh, reqRate);
        return FAILED(hr) ? hr : APOERR_FORMAT_NOT_SUPPORTED;
    }

    // When the engine has already fixed the other side, the pair must agree
    // - this APO is strictly 1:1 in-place (same layout both directions).
    if (pCounterpartFormat != NULL)
    {
        UINT32 otherCh = 0, otherRate = 0;
        hr = GetFloatParams(pCounterpartFormat, &otherCh, &otherRate);
        if (FAILED(hr) || otherCh != reqCh || otherRate != reqRate)
        {
            ApoLog(L"%ls reject: pair mismatch (req %uch @%uHz vs other %uch @%uHz hr=0x%08X)",
                tag, reqCh, reqRate, otherCh, otherRate, hr);
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
    }

    ApoLog(L"%ls accept: %uch @%uHz", tag, reqCh, reqRate);
    return S_OK;
}

HRESULT __stdcall GamerToolAPO::IsInputFormatSupported(IAudioMediaType* pOutputFormat,
    IAudioMediaType* pRequestedInputFormat, IAudioMediaType** ppSupportedInputFormat)
{
    return CheckFloatPair(pOutputFormat, pRequestedInputFormat, ppSupportedInputFormat, L"IsIn");
}

HRESULT __stdcall GamerToolAPO::IsOutputFormatSupported(IAudioMediaType* pInputFormat,
    IAudioMediaType* pRequestedOutputFormat, IAudioMediaType** ppSupportedOutputFormat)
{
    return CheckFloatPair(pInputFormat, pRequestedOutputFormat, ppSupportedOutputFormat, L"IsOut");
}

HRESULT __stdcall GamerToolAPO::GetInputChannelCount(UINT32* pu32ChannelCount)
{
    if (pu32ChannelCount == NULL)
        return E_POINTER;
    if (!m_initialized)
        return APOERR_NOT_INITIALIZED;
    *pu32ChannelCount = (UINT32)(channelCount > 0 ? channelCount : 0);
    return S_OK;
}

HRESULT __stdcall GamerToolAPO::Reset()
{
    // Clear biquad state (not coefficients) - e.g. after a device glitch.
    ResetFilters();
    return S_OK;
}

UINT32 __stdcall GamerToolAPO::CalcInputFrames(UINT32 u32OutputFrameCount)
{
    return u32OutputFrameCount; // 1:1 inplace processing
}

UINT32 __stdcall GamerToolAPO::CalcOutputFrames(UINT32 u32InputFrameCount)
{
    return u32InputFrameCount; // 1:1 inplace processing
}

HRESULT __stdcall GamerToolAPO::LockForProcess(UINT32 u32NumInputConnections,
    APO_CONNECTION_DESCRIPTOR** ppInputConnections, UINT32 u32NumOutputConnections,
    APO_CONNECTION_DESCRIPTOR** ppOutputConnections)
{
    if (!m_initialized)
        return APOERR_NOT_INITIALIZED;
    if (u32NumInputConnections == 0 || u32NumOutputConnections == 0
        || ppInputConnections == NULL || ppOutputConnections == NULL)
        return APOERR_NUM_CONNECTIONS_INVALID;

    // Snapshot the format so APOProcess knows the channel layout/rate.
    // BOTH sides are validated as a pair (float32, identical channels and
    // rate, within the filter bank): processing is strictly 1:1 in-place,
    // so a mismatched pair accepted here would corrupt the interleaved
    // buffer at best and count toward the engine's per-APO failure budget
    // (10 strikes disables the endpoint's SysFx) at worst.
    APO_CONNECTION_DESCRIPTOR* inConn = ppInputConnections[0];
    APO_CONNECTION_DESCRIPTOR* outConn = ppOutputConnections[0];
    if (inConn == NULL || inConn->pFormat == NULL
        || outConn == NULL || outConn->pFormat == NULL)
    {
        ApoLog(L"LockForProcess reject: null connection/format");
        return APOERR_FORMAT_NOT_SUPPORTED;
    }

    UINT32 inCh = 0, inRate = 0, outCh = 0, outRate = 0;
    if (FAILED(GetFloatParams(inConn->pFormat, &inCh, &inRate))
        || FAILED(GetFloatParams(outConn->pFormat, &outCh, &outRate)))
    {
        ApoLog(L"LockForProcess reject: non-float format");
        return APOERR_FORMAT_NOT_SUPPORTED;
    }

    if (inCh == 0 || inCh > (UINT32)MaxChannels || inRate == 0
        || inCh != outCh || inRate != outRate)
    {
        ApoLog(L"LockForProcess reject: pair mismatch (in %uch @%uHz, out %uch @%uHz)",
            inCh, inRate, outCh, outRate);
        return APOERR_FORMAT_NOT_SUPPORTED;
    }

    channelCount = (int)inCh;
    sampleRate = (int)inRate;

    m_locked = true;
    ApoLog(L"LockForProcess ok: %uch @%uHz", channelCount, sampleRate);
    return S_OK;
}

HRESULT __stdcall GamerToolAPO::UnlockForProcess(void)
{
    if (!m_locked)
        return APOERR_ALREADY_UNLOCKED;
    channelCount = 0;
    m_locked = false;
    return S_OK;
}

// ---------------------------------------------------------------------------
// DSP core
// ---------------------------------------------------------------------------
void GamerToolAPO::ResetFilters()
{
    // Zero state; coefficients get designed on the next config snapshot.
    ZeroMemory(filters, sizeof(filters));
}

void GamerToolAPO::DesignBiquad(Biquad& b, float freqHz, float gainDb, float q, int rateHz) const
{
    // RBJ peaking EQ cookbook formula (direct form 1, normalized a0).
    float A = powf(10.0f, gainDb / 40.0f);
    float w0 = 6.28318530717958647692f * freqHz / rateHz;
    float cw = cosf(w0);
    float sw = sinf(w0);
    float alpha = sw / (2.0f * q);

    float a0 = 1.0f + alpha / A;

    b.b0 = (1.0f + alpha * A) / a0;
    b.b1 = (-2.0f * cw) / a0;
    b.b2 = (1.0f - alpha * A) / a0;
    b.a1 = (-2.0f * cw) / a0;
    b.a2 = (1.0f - alpha / A) / a0;
    // NOTE: filter state (z1/z2/z1y/z2y) is deliberately PRESERVED here, not
    // zeroed. Zeroing on every live slider drag caused audible clicks/pops as
    // the UI publishes at drag rate. State is only cleared by ResetFilters()
    // (device glitch / Reset() / construction).
}

void GamerToolAPO::ApplyConfigSnapshot(const float* gainsDb, int enabled)
{
    if (sampleRate <= 0)
        return;

    if (!enabled)
    {
        // Bypass: identity coefficients (b0=1) so state settles immediately.
        for (int c = 0; c < channelCount; c++)
            for (int i = 0; i < 10; i++)
            {
                Biquad& b = filters[c][i];
                b.b0 = 1.0f; b.b1 = 0.0f; b.b2 = 0.0f;
                b.a1 = 0.0f; b.a2 = 0.0f;
            }
    }
    else
    {
        for (int i = 0; i < 10; i++)
        {
            float g = gainsDb[i];
            // Skip flat bands entirely: identity coefficients keep the state
            // untouched and save multiplies - important because most presets
            // only move a few bands.
            bool flat = (g > -0.05f && g < 0.05f);
            for (int c = 0; c < channelCount; c++)
            {
                Biquad& b = filters[c][i];
                if (flat)
                {
                    b.b0 = 1.0f; b.b1 = 0.0f; b.b2 = 0.0f;
                    b.a1 = 0.0f; b.a2 = 0.0f;
                }
                else
                {
                    DesignBiquad(b, BandFrequenciesHz[i], g, BandQ, sampleRate);
                }
            }
        }
    }

    for (int i = 0; i < 10; i++)
        appliedGains[i] = gainsDb[i];
    appliedEnabled = enabled;
}

#pragma AVRT_CODE_BEGIN
void __stdcall GamerToolAPO::APOProcess(UINT32 u32NumInputConnections,
    APO_CONNECTION_PROPERTY** ppInputConnections, UINT32 u32NumOutputConnections,
    APO_CONNECTION_PROPERTY** ppOutputConnections)
{
    if (u32NumInputConnections == 0 || u32NumOutputConnections == 0)
        return;

    float* inputFrames = (float*)ppInputConnections[0]->pBuffer;
    float* outputFrames = (float*)ppOutputConnections[0]->pBuffer;
    UINT32 frameCount = ppInputConnections[0]->u32ValidFrameCount;
    if (frameCount == 0)
        return;

    // Config snapshot: single atomic read; if the writer moved on, we simply
    // use last-applied coefficients this block (writer publishes a stable
    // version with a full memory barrier before flipping it back).
    if (sharedConfig != NULL)
    {
        LONG64 v = InterlockedCompareExchange64(
            (volatile LONG64*)&sharedConfig->version, 0, 0);
        if (v != appliedVersion)
        {
            // Copy the whole block under the assumption the writer keeps it
            // stable across one version increment - it does (single writer).
            EqConfig snap;
            snap.version = v;
            snap.enabled = sharedConfig->enabled;
            for (int i = 0; i < 10; i++)
                snap.gainsDb[i] = sharedConfig->gainsDb[i];

            ApplyConfigSnapshot(snap.gainsDb, snap.enabled);
            appliedVersion = v;
        }
    }

    if (inputFrames != outputFrames)
    {
        // Non-aliasing case: still process in place on the input, then let
        // the engine's shared-buffer mechanics handle the copy-back via
        // the output pointer we write below.
        ProcessFrames(inputFrames, outputFrames, frameCount, channelCount > 0 ? channelCount : 2);
    }
    else
    {
        ProcessFrames(outputFrames, outputFrames, frameCount, channelCount > 0 ? channelCount : 2);
    }

    ppOutputConnections[0]->u32ValidFrameCount = frameCount;
    ppOutputConnections[0]->u32BufferFlags = ppInputConnections[0]->u32BufferFlags;
}
#pragma AVRT_CODE_END

void GamerToolAPO::ProcessFrames(float* input, float* output, UINT32 frameCount, int channels)
{
    int ch = channels;
    UINT32 frames = frameCount;

    if (appliedEnabled == 0)
    {
        // Bypass path: straight copy (or nothing when aliasing).
        if (input != output && frames > 0)
            memcpy(output, input, frames * ch * sizeof(float));
        return;
    }

    for (UINT32 f = 0; f < frames; f++)
    {
        for (int c = 0; c < ch; c++)
        {
            float x = input[f * ch + c];

            // 10 biquads in series, direct form 1. z1/z2 hold the previous
            // two INPUT samples (x history) and z1y/z2y the previous two
            // OUTPUT samples (y history) - four state slots total per section.
            Biquad* chain = filters[c];
            for (int i = 0; i < 10; i++)
            {
                Biquad& b = chain[i];
                float y = b.b0 * x + b.b1 * b.z1 + b.b2 * b.z2
                        - b.a1 * b.z1y - b.a2 * b.z2y;
                b.z2 = b.z1;
                b.z1 = x;
                b.z2y = b.z1y;
                b.z1y = y;
                x = y;
            }

            output[f * ch + c] = x;
        }
    }
}
