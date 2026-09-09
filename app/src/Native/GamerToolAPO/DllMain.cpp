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

// Registration is handled by the SDK's RegisterAPO helper (serializes the
// binary APO_REG_PROPERTIES blob audiodg expects under
// HKCR\AudioEngine\AudioProcessingObjects). No hand-rolled blob writing.
static HRESULT WriteApoRegistration()
{
    return RegisterAPO(GamerToolAPO::regProperties);
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
// this DLL to ProgramData. RegisterAPO (SDK helper in BaseAudioProcessingObject)
// writes the binary APO_REG_PROPERTIES blob audiodg expects; doing it here
// guarantees byte-exact serialization that a C# reimplementation would risk
// getting subtly wrong.
// ---------------------------------------------------------------------------

extern "C" __declspec(dllexport)
int __stdcall GamerToolApoRegister(wchar_t* dllPath)
{
    // RegisterAPO serializes regProperties (CLSID, flags, version, name,
    // copyright, the GUID list) under HKCR\AudioEngine\AudioProcessingObjects.
    HRESULT hr = RegisterAPO(GamerToolAPO::regProperties);
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
    UnregisterAPO(CLSID_GamerToolAPO);

    wchar_t clsidStr[64];
    StringFromGUID2(CLSID_GamerToolAPO, clsidStr, 64);
    std::wstring base = L"SOFTWARE\\Classes\\CLSID\\";
    base += clsidStr;
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, base.c_str());
    return 0;
}
