/*
    GamerToolAPO - built-in system-wide 10-band equalizer for Gamer Tool.

    A minimal Audio Processing Object (APO) loaded by audiodg.exe. It runs a
    chain of 10 peaking biquad filters (one per ISO band, constant-Q) on every
    audio frame that flows through the endpoint it is registered on.

    Design constraints (this code runs on the real-time audio thread):
      - No heap allocation in APOProcess
      - No locks in APOProcess
      - No syscalls in APOProcess beyond the memory barrier of an atomic read

    Parameter delivery: the GamerTool.exe UI writes an EqConfig (gain vector +
    version counter) into a fixed-name file-mapping section created at DLL
    load time. APOProcess snapshots the config ONCE via an atomic version
    check per processing call; recompute of biquad coefficients happens
    inside the swapped-in snapshot copy, never on live state.

    SPDX-License-Identifier: GPL-2.0-or-later
    Biquad/COM structure informed by EqualizerAPO (Copyright 2012 Jonas
    Thedering) and Microsoft's AudioProcessingObjects SDK samples.
*/

#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <audioclient.h>
#include <audioenginebaseapo.h>
#include <BaseAudioProcessingObject.h>
#include <mmdeviceapi.h>
#include <mmreg.h>
#include <new>

// {B7C1B0CB-4D2A-4E48-9B3B-42C39EB0F316}  GamerToolAPO post-mix CLSID
// Generated once, stable forever - persisted in endpoint FxProperties.
DEFINE_GUID(CLSID_GamerToolAPO,
    0xb7c1b0cb, 0x4d2a, 0x4e48, 0x9b, 0x3b, 0x42, 0xc3, 0x9e, 0xb0, 0xf3, 0x16);

// The 10 ISO band center frequencies Gamer Tool's UI exposes, in Hz.
static const float BandFrequenciesHz[10] =
    { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

// Q factor for each peaking band - 1.414 is the classic octave-spacing value.
static const float BandQ = 1.414f;

#pragma pack(push, 1)
// Shared parameter block. Layout is frozen - the C# side mirrors it exactly.
// Written by GamerTool.exe, read by every APO instance in audiodg.exe.
struct EqConfig
{
    volatile LONG64 version;   // incremented by writer before/after each publish; 0 = flat
    int enabled;               // 0 = bypass (flat), 1 = process
    float gainsDb[10];         // per-band gain in dB, -12..+12
};
#pragma pack(pop)

// One biquad section in direct form 1: separate input and output history
// (four state slots) - the classic DF1 structure.
struct Biquad
{
    float b0, b1, b2;   // feedforward
    float a1, a2;       // feedback (normalized: a0 == 1)
    float z1, z2;       // input history
    float z1y, z2y;     // output history
};

class GamerToolAPO : public CBaseAudioProcessingObject, public IAudioSystemEffects
{
public:
    GamerToolAPO(IUnknown* pUnkOuter);
    virtual ~GamerToolAPO();

    // IUnknown (delegating - aggregation-compatible, same pattern as EqualizerAPO)
    HRESULT __stdcall QueryInterface(const IID& iid, void** ppv) override;
    ULONG __stdcall AddRef() override;
    ULONG __stdcall Release() override;

    // IAudioProcessingObject
    HRESULT __stdcall GetLatency(HNSTIME* pTime) override;
    HRESULT __stdcall Initialize(UINT32 cbDataSize, BYTE* pbyData) override;
    HRESULT __stdcall IsInputFormatSupported(IAudioMediaType* pOutputFormat,
        IAudioMediaType* pRequestedInputFormat, IAudioMediaType** ppSupportedInputFormat) override;
    HRESULT __stdcall Reset() override;

    // IAudioProcessingObjectConfiguration
    HRESULT __stdcall LockForProcess(UINT32 u32NumInputConnections,
        APO_CONNECTION_DESCRIPTOR** ppInputConnections, UINT32 u32NumOutputConnections,
        APO_CONNECTION_DESCRIPTOR** ppOutputConnections) override;
    HRESULT __stdcall UnlockForProcess(void) override;

    // IAudioProcessingObjectRT - runs on the audio thread
    void __stdcall APOProcess(UINT32 u32NumInputConnections,
        APO_CONNECTION_PROPERTY** ppInputConnections, UINT32 u32NumOutputConnections,
        APO_CONNECTION_PROPERTY** ppOutputConnections) override;

    // Registration data used by DllRegisterServer and the C# bootstrap
    static const CRegAPOProperties<1> regProperties;

private:
    long        refCount;
    IUnknown*   pUnkOuter;

    // Opened once at Initialize (not RT), read-only mapping thereafter.
    HANDLE      mappingHandle;
    EqConfig*   sharedConfig;

    // Per-instance processing state (per channel: 10 biquads).
    static const int MaxChannels = 8;
    Biquad      filters[MaxChannels][10];
    int         channelCount;
    int         sampleRate;

    // Snapshot machinery: configVersion last applied, and the gains it held.
    LONG64      appliedVersion;
    float       appliedGains[10];
    int         appliedEnabled;

    void ApplyConfigSnapshot(const float* gainsDb, int enabled);
    void DesignBiquad(Biquad& b, float freqHz, float gainDb, float q, int sampleRate) const;
    void ProcessFrames(float* input, float* output, UINT32 frameCount, int channels);
    void ResetFilters();

    HANDLE OpenSharedConfig();

    // Non-delegating IUnknown for aggregation (macro from BaseAudioProcessingObject.h).
    BEGIN_APO_OBJECT_MAP()
    END_APO_OBJECT_MAP()
};
