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
    appliedPreampLinear = 1.0f;
    appliedCompressionAmount = 0.0f;
    compressorAttackCoeff = 0.0f;
    compressorReleaseCoeff = 0.0f;
    ZeroMemory(appliedGains, sizeof(appliedGains));
    ZeroMemory(compressorEnvelope, sizeof(compressorEnvelope));
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
        m_initialized = true;
        return S_OK;
    }

    // Fallback for raw/generic init blobs: don't fail the graph.
    m_initialized = true;
    return S_OK;
}

HRESULT __stdcall GamerToolAPO::GetLatency(HNSTIME* pTime)
{
    if (!pTime)
        return E_POINTER;
    if (!m_locked)
        return APOERR_ALREADY_UNLOCKED;
    *pTime = 0; // biquad is pure feedforward state, adds no latency
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

// Accept only 32-bit float - the one format the engine offers LFX APOs and
// the one our biquads process in place.
static HRESULT CheckFloat32Format(IAudioMediaType* pRequestedFormat, IAudioMediaType** ppSupportedFormat)
{
    if (pRequestedFormat == NULL || ppSupportedFormat == NULL)
        return E_POINTER;
    *ppSupportedFormat = NULL;

    UNCOMPRESSEDAUDIOFORMAT fmt;
    ZeroMemory(&fmt, sizeof(fmt));
    HRESULT hr = pRequestedFormat->GetUncompressedAudioFormat(&fmt);
    if (FAILED(hr))
        return APOERR_FORMAT_NOT_SUPPORTED;

    if (fmt.guidFormatType != KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
        return APOERR_FORMAT_NOT_SUPPORTED;

    pRequestedFormat->AddRef();
    *ppSupportedFormat = pRequestedFormat;
    return S_OK;
}

HRESULT __stdcall GamerToolAPO::IsInputFormatSupported(IAudioMediaType* pOutputFormat,
    IAudioMediaType* pRequestedInputFormat, IAudioMediaType** ppSupportedInputFormat)
{
    UNREFERENCED_PARAMETER(pOutputFormat);
    if (pRequestedInputFormat == NULL || ppSupportedInputFormat == NULL)
        return E_POINTER;
    return CheckFloat32Format(pRequestedInputFormat, ppSupportedInputFormat);
}

HRESULT __stdcall GamerToolAPO::IsOutputFormatSupported(IAudioMediaType* pInputFormat,
    IAudioMediaType* pRequestedOutputFormat, IAudioMediaType** ppSupportedOutputFormat)
{
    UNREFERENCED_PARAMETER(pInputFormat);
    if (pRequestedOutputFormat == NULL || ppSupportedOutputFormat == NULL)
        return E_POINTER;
    return CheckFloat32Format(pRequestedOutputFormat, ppSupportedOutputFormat);
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
    UNREFERENCED_PARAMETER(ppOutputConnections);
    if (!m_initialized)
        return APOERR_NOT_INITIALIZED;
    if (u32NumInputConnections == 0 || u32NumOutputConnections == 0
        || ppInputConnections == NULL || ppOutputConnections == NULL)
        return APOERR_NUM_CONNECTIONS_INVALID;

    // Snapshot the format so APOProcess knows the channel layout/rate.
    APO_CONNECTION_DESCRIPTOR* inConn = ppInputConnections[0];
    if (inConn == NULL || inConn->pFormat == NULL)
        return APOERR_FORMAT_NOT_SUPPORTED;

    UNCOMPRESSEDAUDIOFORMAT fmt;
    ZeroMemory(&fmt, sizeof(fmt));
    if (FAILED(inConn->pFormat->GetUncompressedAudioFormat(&fmt)))
        return APOERR_FORMAT_NOT_SUPPORTED;
    if (fmt.guidFormatType != KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
        return APOERR_FORMAT_NOT_SUPPORTED;

    channelCount = (int)fmt.dwSamplesPerFrame;
    if (channelCount > MaxChannels)
        channelCount = MaxChannels;
    if (channelCount <= 0)
        return APOERR_FORMAT_NOT_SUPPORTED;
    sampleRate = (int)fmt.fFramesPerSecond;
    if (sampleRate <= 0)
        return APOERR_FORMAT_NOT_SUPPORTED;

    m_locked = true;
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
    ZeroMemory(compressorEnvelope, sizeof(compressorEnvelope));
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

// "Balance Loud & Quiet Sounds" - a simple downward compressor. amount=0 is
// pure identity (returns 1.0f unconditionally, so the feature costs nothing
// when untouched); amount=1 is the most aggressive setting. Threshold and
// ratio both slide linearly with amount rather than exposing either as a
// separate control - the UI shows one plain-English slider, not a mixing
// console. Never increases gain above 1.0x on its own (only ever pulls loud
// peaks down, partially made back up), so a quiet channel never gets noisier.
static inline float ComputeCompressorGain(float envelopeLinear, float amount)
{
    if (amount <= 0.0001f)
        return 1.0f;

    float thresholdDb = -24.0f * amount;      // 0dB (never engages) .. -24dB
    float ratio = 1.0f + 5.0f * amount;       // 1:1 (no effect) .. 6:1

    float envDb = 20.0f * log10f(envelopeLinear > 1e-6f ? envelopeLinear : 1e-6f);
    if (envDb <= thresholdDb)
        return 1.0f;

    float overDb = envDb - thresholdDb;
    float gainReductionDb = overDb - (overDb / ratio);

    // Partial makeup gain: brings the compressed peak back up part-way so
    // this reads as "balanced", not just "everything got quieter".
    float makeupDb = gainReductionDb * 0.5f;

    return powf(10.0f, (makeupDb - gainReductionDb) / 20.0f);
}

void GamerToolAPO::ApplyConfigSnapshot(const float* gainsDb, int enabled, float preampDb, float compressionAmount)
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

    // Preamp: dB -> linear once here, not per-sample.
    appliedPreampLinear = powf(10.0f, preampDb / 20.0f);

    // Compressor: clamp defensively (a corrupt/uninitialized 0-byte section
    // would read as 0.0f, which is "off" - the safe direction) and recompute
    // the attack/release smoothing coefficients for the current sample rate.
    // 5ms attack catches transients fast enough to matter for footsteps/gunshots;
    // 150ms release is slow enough to avoid audible "pumping".
    appliedCompressionAmount = (compressionAmount < 0.0f) ? 0.0f : (compressionAmount > 1.0f ? 1.0f : compressionAmount);
    compressorAttackCoeff = 1.0f - expf(-1.0f / (0.005f * (float)sampleRate));
    compressorReleaseCoeff = 1.0f - expf(-1.0f / (0.150f * (float)sampleRate));
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
        // Heartbeat: written EVERY call, unconditionally (bypass or not) -
        // this is the one fact that actually proves audiodg loaded this APO
        // and is calling it. If GamerTool.exe ever sees this value stop
        // moving, the APO isn't running at all (most likely cause: Windows
        // silently refusing to load an unsigned APO into the protected
        // audiodg.exe process) - a completely different problem from "the
        // APO is running but set to flat", which is what a zeroed gains
        // array alone would otherwise look identical to from the outside.
        InterlockedExchange64((volatile LONG64*)&sharedConfig->heartbeatTicks, (LONG64)GetTickCount64());

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
            snap.preampDb = sharedConfig->preampDb;
            snap.compressionAmount = sharedConfig->compressionAmount;

            ApplyConfigSnapshot(snap.gainsDb, snap.enabled, snap.preampDb, snap.compressionAmount);
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

            // Preamp: single dB-derived linear gain, precomputed per snapshot
            // (not per-sample) in ApplyConfigSnapshot.
            x *= appliedPreampLinear;

            // "Balance Loud & Quiet Sounds": envelope-follow this channel's
            // post-EQ/preamp level, then pull loud peaks down toward the
            // quieter material. Skipped entirely at amount=0 (default).
            if (appliedCompressionAmount > 0.0001f)
            {
                int envCh = (c < MaxChannels) ? c : 0;
                float absX = fabsf(x);
                float& env = compressorEnvelope[envCh];
                float coeff = (absX > env) ? compressorAttackCoeff : compressorReleaseCoeff;
                env += coeff * (absX - env);

                x *= ComputeCompressorGain(env, appliedCompressionAmount);
            }

            // Final safety clamp: whatever the EQ/preamp/compressor did,
            // never hand audiodg a sample outside [-1, 1]. A hard clip here
            // is inaudible in the rare case it triggers; letting an out-of-
            // range sample through could clip audibly further down the chain.
            if (x > 1.0f) x = 1.0f;
            else if (x < -1.0f) x = -1.0f;

            output[f * ch + c] = x;
        }
    }
}
