/*
    GamerToolAPO - implementation. See GamerToolAPO.h for the architecture
    overview. RT-safety rules are absolute for everything under the
    #pragma AVRT_CODE markers.
*/

#include "stdafx.h"
#include "GamerToolAPO.h"
#include "ClassFactory.h"
#include <Unknwn.h>
#include <math.h>

#define GAMERTOOL_SHARED_MEMORY_NAME L"Local\\GamerToolEqConfig"

// ---------------------------------------------------------------------------
// Registration metadata - CRegAPOProperties<1> declares one APO CLSID.
// APO_FLAG_INPLACE lets us process directly into the output buffer when the
// engine gives us that luxury (both connection buffers may alias).
// ---------------------------------------------------------------------------
const CRegAPOProperties<1> GamerToolAPO::regProperties(
    CLSID_GamerToolAPO, L"GamerToolAPO", L"Gamer Tool built-in equalizer", 1, 0,
    __uuidof(IAudioProcessingObject),
    (APO_FLAG)(APO_FLAG_FRAMESPERSECOND_MUST_MATCH | APO_FLAG_BITSPERSAMPLE_MUST_MATCH | APO_FLAG_INPLACE));

// ---------------------------------------------------------------------------
// CBaseAudioProcessingObject requires these helpers.
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
    : CBaseAudioProcessingObject(regProperties)
{
    m_refCount = 1;

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
    // GamerTool.exe opens the same name when applying presets. A NULL DACL
    // lets any integrity-level process map it read/write, but Local\ scoping
    // confines it to this login session.
    HANDLE h = CreateFileMappingW(
        INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE,
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

    // Accept both the classic APOInitSystemEffects and the Windows 10+
    // APOInitSystemEffects2 (which carries the device GUID we don't need -
    // all devices share the one Gamer Tool config).
    if (cbDataSize == sizeof(APOInitSystemEffects) || cbDataSize == sizeof(APOInitSystemEffects2))
    {
        // Open (or create) the shared config at first initialize.
        if (sharedConfig == NULL)
        {
            mappingHandle = OpenSharedConfig();
            if (mappingHandle != NULL)
            {
                sharedConfig = (EqConfig*)MapViewOfFile(
                    mappingHandle, FILE_MAP_READ, 0, 0, sizeof(EqConfig));
            }
        }
        return S_OK;
    }

    // Fallback for raw/generic init blobs: don't fail the graph.
    return S_OK;
}

HRESULT __stdcall GamerToolAPO::GetLatency(HNSTIME* pTime)
{
    if (!pTime)
        return E_POINTER;
    if (!m_bIsLocked)
        return APOERR_ALREADY_UNLOCKED;
    *pTime = 0; // biquad is pure feedforward state, adds no latency
    return S_OK;
}

HRESULT __stdcall GamerToolAPO::IsInputFormatSupported(IAudioMediaType* pOutputFormat,
    IAudioMediaType* pRequestedInputFormat, IAudioMediaType** ppSupportedInputFormat)
{
    // The engine calls the base-class helper, which applies the flags we
    // declared (sample rate + bits must match). We accept anything float32.
    return CBaseAudioProcessingObject::IsInputFormatSupported(
        pOutputFormat, pRequestedInputFormat, ppSupportedInputFormat);
}

HRESULT __stdcall GamerToolAPO::Reset()
{
    // Clear biquad state (not coefficients) - e.g. after a device glitch.
    ResetFilters();
    return S_OK;
}

HRESULT __stdcall GamerToolAPO::LockForProcess(UINT32 u32NumInputConnections,
    APO_CONNECTION_DESCRIPTOR** ppInputConnections, UINT32 u32NumOutputConnections,
    APO_CONNECTION_DESCRIPTOR** ppOutputConnections)
{
    HRESULT hr = CBaseAudioProcessingObject::LockForProcess(
        u32NumInputConnections, ppInputConnections,
        u32NumOutputConnections, ppOutputConnections);
    if (FAILED(hr))
        return hr;

    // Snapshot the format so APOProcess knows the channel layout/rate.
    APO_CONNECTION_DESCRIPTOR* inConn = ppInputConnections[0];
    UNCOMPRESSEDAUDIOFORMAT fmt;
    IAudioMediaType* mt = inConn->pFormat;
    if (mt != NULL && SUCCEEDED(mt->GetUncompressedAudioFormat(&fmt)))
    {
        channelCount = (int)fmt.dwSamplesPerFrame;
        if (channelCount > MaxChannels)
            channelCount = MaxChannels;
        sampleRate = (int)fmt.fFramesPerSecond;
    }
    return S_OK;
}

HRESULT __stdcall GamerToolAPO::UnlockForProcess(void)
{
    channelCount = 0;
    return CBaseAudioProcessingObject::UnlockForProcess();
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
