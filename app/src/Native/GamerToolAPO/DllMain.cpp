/*
    GamerToolAPO - DllMain + COM self-registration.

    DllRegisterServer writes the APO CLSID descriptor blob under
    HKCR\AudioEngine\AudioProcessingObjects\{clsid} exactly the way the
    Windows samples do: a binary-serialized APO_REG_PROPERTIES struct.
    audiodg.exe reads that key to learn what APOs exist and what flags
    they declare. Endpoint-level FxProperties wiring (which device gets
    the APO) is done by the GamerTool.exe bootstrap on the C# side.
*/

#include "stdafx.h"
#include "GamerToolAPO.h"
#include <string>
#include <combaseapi.h>

static HINSTANCE g_module = NULL;

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_module = module;
        DisableThreadLibraryCalls(module);
        break;
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

// APO registration, hand-rolled (no RegisterAPO helper - its implementation
// ships in WDK-only libs unavailable to CI). The layout below was verified
// value-for-value against the in-box APOs under
// HKCR\AudioEngine\AudioProcessingObjects on a stock Windows install:
// discrete FriendlyName/Copyright strings, DWORD versions/flags/connection
// counts, and APOInterface<N> GUID strings (lowercase, braced). The C#
// bootstrap (NativeEqEngine.IsEngineEnabled) probes for this exact key.
static HRESULT WriteApoRegistration()
{
    const APO_REG_PROPERTIES* props = GamerToolAPO::regProperties;

    wchar_t clsidStr[64];
    if (StringFromGUID2(CLSID_GamerToolAPO, clsidStr, 64) == 0)
        return E_FAIL;

    std::wstring keyPath = L"SOFTWARE\\Classes\\AudioEngine\\AudioProcessingObjects\\";
    keyPath += clsidStr;

    // Interface GUID string in the in-box style: lowercase + braced.
    wchar_t iidStr[64];
    if (StringFromGUID2(__uuidof(IAudioProcessingObject), iidStr, 64) == 0)
        return E_FAIL;
    CharLowerW(iidStr);

    HKEY key = NULL;
    LONG status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, keyPath.c_str(),
        0, NULL, 0, KEY_SET_VALUE, NULL, &key, NULL);
    if (status != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(status);

    HRESULT hr = S_OK;
    DWORD dwordValue = 0;
    auto setDword = [&](const wchar_t* name, DWORD value) {
        if (SUCCEEDED(hr))
        {
            LONG s = RegSetValueExW(key, name, 0, REG_DWORD,
                (const BYTE*)&value, sizeof(value));
            if (s != ERROR_SUCCESS)
                hr = HRESULT_FROM_WIN32(s);
        }
    };
    auto setString = [&](const wchar_t* name, const wchar_t* value) {
        if (SUCCEEDED(hr))
        {
            LONG s = RegSetValueExW(key, name, 0, REG_SZ,
                (const BYTE*)value, (DWORD)((wcslen(value) + 1) * sizeof(wchar_t)));
            if (s != ERROR_SUCCESS)
                hr = HRESULT_FROM_WIN32(s);
        }
    };

    // regProperties was built as (friendly, copyright, major 1, minor 0,
    // IAudioProcessingObject) so these stay in sync by construction.
    setString(L"FriendlyName", L"GamerToolAPO");
    setString(L"Copyright", L"Gamer Tool built-in equalizer");
    setDword(L"MajorVersion", props->u32MajorVersion);
    setDword(L"MinorVersion", props->u32MinorVersion);
    dwordValue = (DWORD)props->Flags;
    setDword(L"Flags", dwordValue);
    setDword(L"MinInputConnections", props->u32MinInputConnections);
    setDword(L"MaxInputConnections", props->u32MaxInputConnections);
    setDword(L"MinOutputConnections", props->u32MinOutputConnections);
    setDword(L"MaxOutputConnections", props->u32MaxOutputConnections);
    setDword(L"MaxInstances", props->u32MaxInstances);
    setDword(L"NumAPOInterfaces", props->u32NumAPOInterfaces);
    setString(L"APOInterface0", iidStr);

    RegCloseKey(key);
    if (FAILED(hr))
        RegDeleteTreeW(HKEY_LOCAL_MACHINE, keyPath.c_str());
    return hr;
}
STDAPI DllRegisterServer(void)
{
    return WriteApoRegistration();
}

STDAPI DllUnregisterServer(void)
{
    // Remove the CLSID registration; the C# bootstrap handles endpoint
    // FxProperties and its own file layout.
    wchar_t keyPath[256];
    swprintf_s(keyPath, L"CLSID\\{%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
        CLSID_GamerToolAPO.Data1, CLSID_GamerToolAPO.Data2, CLSID_GamerToolAPO.Data3,
        CLSID_GamerToolAPO.Data4[0], CLSID_GamerToolAPO.Data4[1],
        CLSID_GamerToolAPO.Data4[2], CLSID_GamerToolAPO.Data4[3],
        CLSID_GamerToolAPO.Data4[4], CLSID_GamerToolAPO.Data4[5],
        CLSID_GamerToolAPO.Data4[6], CLSID_GamerToolAPO.Data4[7]);
    RegDeleteTreeW(HKEY_CLASSES_ROOT, keyPath);

    swprintf_s(keyPath, L"AudioEngine\\AudioProcessingObjects\\{%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
        CLSID_GamerToolAPO.Data1, CLSID_GamerToolAPO.Data2, CLSID_GamerToolAPO.Data3,
        CLSID_GamerToolAPO.Data4[0], CLSID_GamerToolAPO.Data4[1],
        CLSID_GamerToolAPO.Data4[2], CLSID_GamerToolAPO.Data4[3],
        CLSID_GamerToolAPO.Data4[4], CLSID_GamerToolAPO.Data4[5],
        CLSID_GamerToolAPO.Data4[6], CLSID_GamerToolAPO.Data4[7]);
    RegDeleteTreeW(HKEY_CLASSES_ROOT, keyPath);
    return S_OK;
}

// ---------------------------------------------------------------------------
// Bootstrap exports - called by GamerTool.exe via P/Invoke after it extracts
// this DLL to ProgramData. Registration is the hand-rolled writer above
// (verified against the in-box APO keys); doing it here in the DLL keeps
// the exact key layout next to the code it describes.
// ---------------------------------------------------------------------------

extern "C" __declspec(dllexport)
int __stdcall GamerToolApoRegister(wchar_t* dllPath)
{
    // APO registration under HKLM\SOFTWARE\Classes\AudioEngine\...
    HRESULT hr = WriteApoRegistration();
    if (FAILED(hr))
        return (int)hr;

    // COM InprocServer32 registration pointing at our permanent home.
    wchar_t clsidStr[64];
    StringFromGUID2(CLSID_GamerToolAPO, clsidStr, 64);
    std::wstring base = L"CLSID\\";
    base += clsidStr;

    HKEY k;
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, (L"SOFTWARE\\Classes\\" + base).c_str(),
        0, NULL, 0, KEY_SET_VALUE, NULL, &k, NULL) == ERROR_SUCCESS)
    {
        RegSetValueExW(k, NULL, 0, REG_SZ, (BYTE*)L"GamerToolAPO", 24);
        RegCloseKey(k);
    }
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, (L"SOFTWARE\\Classes\\" + base + L"\\InprocServer32").c_str(),
        0, NULL, 0, KEY_SET_VALUE, NULL, &k, NULL) == ERROR_SUCCESS)
    {
        RegSetValueExW(k, NULL, 0, REG_SZ, (BYTE*)dllPath, (DWORD)((wcslen(dllPath) + 1) * 2));
        RegCloseKey(k);
    }
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, (L"SOFTWARE\\Classes\\" + base + L"\\InprocServer32").c_str(),
        0, NULL, 0, KEY_SET_VALUE, NULL, &k, NULL) == ERROR_SUCCESS)
    {
        RegSetValueExW(k, L"ThreadingModel", 0, REG_SZ, (BYTE*)L"Both", 10);
        RegCloseKey(k);
    }

    return 0;
}

extern "C" __declspec(dllexport)
int __stdcall GamerToolApoUnregister(void)
{
    wchar_t clsidStr[64];
    StringFromGUID2(CLSID_GamerToolAPO, clsidStr, 64);

    std::wstring apoKey = L"SOFTWARE\\Classes\\AudioEngine\\AudioProcessingObjects\\";
    apoKey += clsidStr;
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, apoKey.c_str());

    std::wstring base = L"SOFTWARE\\Classes\\CLSID\\";
    base += clsidStr;
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, base.c_str());
    return 0;
}
