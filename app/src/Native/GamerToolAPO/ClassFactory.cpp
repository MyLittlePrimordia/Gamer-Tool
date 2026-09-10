/*
    GamerToolAPO - standard COM class factory for the APO CLSID, plus the
    four required DLL exports.
*/

#include "stdafx.h"
#include "GamerToolAPO.h"
#include "ClassFactory.h"

static long g_lockCount = 0;
static long g_objCount = 0;

class GamerToolClassFactory : public IClassFactory
{
public:
    GamerToolClassFactory() : refCount(1) {}

    HRESULT __stdcall QueryInterface(const IID& iid, void** ppv) override
    {
        if (ppv == NULL)
            return E_POINTER;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = NULL;
        return E_NOINTERFACE;
    }

    ULONG __stdcall AddRef() override
    {
        return InterlockedIncrement(&refCount);
    }

    ULONG __stdcall Release() override
    {
        long r = InterlockedDecrement(&refCount);
        if (r == 0)
            delete this;
        return r;
    }

    HRESULT __stdcall CreateInstance(IUnknown* pUnkOuter, const IID& iid, void** ppv) override
    {
        // Aggregation requires requesting IUnknown on creation.
        if (pUnkOuter != NULL && iid != __uuidof(IUnknown))
            return CLASS_E_NOAGGREGATION;

        IUnknown* obj = NULL;
        HRESULT hr = CreateGamerToolAPO(pUnkOuter, &obj);
        if (FAILED(hr))
            return hr;

        hr = obj->QueryInterface(iid, ppv);
        obj->Release();
        return hr;
    }

    HRESULT __stdcall LockServer(BOOL lock) override
    {
        if (lock)
            InterlockedIncrement(&g_lockCount);
        else
            InterlockedDecrement(&g_lockCount);
        return S_OK;
    }

private:
    long refCount;
};

HRESULT __stdcall DllGetClassObject(const CLSID& clsid, const IID& iid, void** ppv)
{
    if (ppv == NULL)
        return E_POINTER;
    if (clsid != CLSID_GamerToolAPO)
        return CLASS_E_CLASSNOTAVAILABLE;

    *ppv = static_cast<IClassFactory*>(new (std::nothrow) GamerToolClassFactory());
    if (*ppv == NULL)
        return E_OUTOFMEMORY;
    // ctor already AddRef'd to 1 - do NOT AddRef again; QI next.
    HRESULT hr = static_cast<IClassFactory*>(*ppv)->QueryInterface(iid, ppv);
    if (FAILED(hr))
    {
        delete static_cast<GamerToolClassFactory*>(*ppv);
        *ppv = NULL;
    }
    return hr;
}

HRESULT __stdcall DllCanUnloadNow()
{
    return (g_lockCount == 0 && g_objCount == 0) ? S_OK : S_FALSE;
}
