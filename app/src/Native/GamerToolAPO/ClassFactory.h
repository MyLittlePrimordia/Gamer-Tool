/*
    GamerToolAPO - COM class factory + DllMain. audiodg.exe CoCreateInstance's
    CLSID_GamerToolAPO; this returns the APO object (with aggregation support
    via the non-delegating unknown pattern used by all system-effect APOs).
*/

#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <Unknwn.h>
#include <audioclient.h>
#include <audioenginebaseapo.h>
#include <BaseAudioProcessingObject.h>

// The non-delegating unknown base our APO aggregates. The APO base header
// provides the macro implementations; this abstract shell is how
// EqualizerAPO and the Windows samples structure aggregation.
class INonDelegatingUnknownBase
{
public:
    virtual HRESULT __stdcall NonDelegatingQueryInterface(const IID& iid, void** ppv) = 0;
    virtual ULONG __stdcall NonDelegatingAddRef() = 0;
    virtual ULONG __stdcall NonDelegatingRelease() = 0;
};
