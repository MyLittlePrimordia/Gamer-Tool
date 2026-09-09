/*
    GamerToolAPO - standard COM class factory for the APO CLSID plus the
    four required DLL exports (see ClassFactory.cpp). Plain IUnknown
    refcounting throughout - no ATL dependency, static CRT only.
*/

#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <Unknwn.h>
