# Gamer Tool

A portable Windows utility combining a driverless display-tuning engine
(brightness/contrast/gamma/black-equalizer/RGB) with a system-wide 10-band
audio EQ (via Equalizer APO), global hotkeys, and combo presets — built as a
single self-contained `.exe` with no installer.

## Why this won't mute your audio like the last attempt

The earlier failure happened because `audiodg.exe` (the Windows audio engine
host) runs as `NT AUTHORITY\LOCAL SERVICE`, and the EQ config file lived
somewhere LOCAL SERVICE couldn't read (`%APPDATA%`/`%USERPROFILE%`). When an
APO can't read its config during the real-time audio callback, Windows mutes
the device as a failsafe.

This build fixes that at the root:

- EQ config always lives at `C:\ProgramData\GamerTool\EQ\config.txt`.
- On first use, GamerTool does a **one-time elevated setup** (`icacls`
  granting `NT SERVICE\Audiosrv` and `LOCAL SERVICE` read+execute on that
  folder) — see `Services/AudioService.cs`.
- The app itself runs as a normal user at every other launch — it only
  re-launches itself elevated (`--elevated-audio-setup`) for that one setup
  step, and again briefly for `--elevated-panic-reset` if you hit the Panic
  button (which also restarts `audiosrv` as a last resort).
- Config writes go through a temp-file + replace so Equalizer APO's file
  watcher never reads a half-written file.

Display tuning needs **no admin at all** — it's pure user-mode GDI
(`SetDeviceGammaRamp`), so that tab works immediately on a fresh install.

## Audio engine setup — now fully automated

Clicking **"Enable Audio EQ"** now does everything in one elevated pass
(`Services/EqualizerApoInstallerService.cs`):

1. Checks whether Equalizer APO is already installed (checking both the
   64-bit and WOW6432Node registry views, since 32-bit installers get
   silently redirected there).
2. If missing, downloads the current Windows installer from SourceForge's
   stable `files/latest/download` redirector and sanity-checks it (size +
   PE header) before running it.
3. Silently installs the engine (`Setup.exe /S`).
4. Resolves your current default playback device's endpoint GUID and writes
   the `FxProperties` registry entry (`PKEY_FX_EndpointEffectClsid` →
   Equalizer APO's Post-Mix CLSID) that Configurator.exe's checkbox would
   otherwise write — this is the same live audio-engine key documented at
   https://github.com/dechamps/APO.
5. That key's parent (`MMDevices\Audio\...`) is owned by `TrustedInstaller`,
   so even an elevated admin can't create `FxProperties` on a device that's
   never had one. `Services/RegistryOwnershipHelper.cs` scripts the same
   ownership-transfer regedit normally requires (`SeTakeOwnershipPrivilege`
   + reassigning to Administrators), scoped to just that one device's key.
6. Restarts `audiosrv` so the change takes effect.

**Built-in safety backstops**, because guessing wrong here is exactly the
failure mode this whole project exists to avoid:

- If your default device already has a *different* effect configured (i.e.
  you're already running some other audio enhancement tool), GamerTool does
  **not** overwrite it — it opens Equalizer APO's own Configurator instead
  so you can decide.
- If the automatic registry write fails for any other reason, same
  fallback: Configurator opens, elevated, so you can finish with an official
  checkbox instead of GamerTool guessing further.
- Every step is logged to `C:\ProgramData\GamerTool\EQ\setup.log`.
- `EqualizerApoInstallerService.UnregisterEfxForEndpoint(guid)` cleanly
  removes just GamerTool's registration if you ever want to back out,
  without touching anything else on the device.

No separate manual Equalizer APO install is required anymore — that was
the old flow, now superseded by the above.

## Build

```powershell
dotnet publish GamerTool/GamerTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish
```

Or just push to `main` — `.github/workflows/build.yml` does this on
`windows-latest` and uploads `GamerTool.exe` as a build artifact.

## Project layout

```
GamerTool/
├── GamerTool.csproj        .NET 9 WPF, single-file publish settings
├── app.manifest             asInvoker (no forced UAC at launch)
├── App.xaml / App.xaml.cs   routes elevated-helper relaunches, no window for those
├── MainWindow.xaml(.cs)     Display / Audio / Hotkeys tabs
├── Models/                  DisplayPreset, AudioPreset, ComboPreset, HotkeyBinding, AppSettings
├── Services/
│   ├── DisplayService.cs     GDI gamma ramp + black-equalizer math
│   ├── FocusWatcher.cs       re-asserts gamma ramp for fullscreen-exclusive games
│   ├── AudioService.cs       Equalizer APO config writer, ACL fix, elevated setup entry point, panic reset
│   ├── EqualizerApoInstallerService.cs  downloads/installs Equalizer APO, registers it per device
│   ├── RegistryOwnershipHelper.cs       scripted TrustedInstaller ownership takeover for MMDevices keys
│   ├── AudioDeviceService.cs WASAPI render-endpoint enumeration (COM interop)
│   ├── HotkeyService.cs     Win32 RegisterHotKey wrapper
│   └── ProfileManager.cs    JSON persistence under %AppData%\GamerTool
└── UI/
    ├── OsdNotification.xaml(.cs)     click-through in-game HUD toast
    ├── HotkeyCaptureWindow.xaml(.cs) key-combo recorder
    ├── InputDialog.xaml(.cs)         name-entry modal
    ├── CustomSliders.xaml            EQ band + HUD slider styles
    └── Converters.cs
```

## Known gaps / next steps

- Equalizer APO's installer is unsigned, so there's no official checksum to
  verify the download against — GamerTool only sanity-checks size + PE
  header, not authenticity. If that matters for your threat model, install
  Equalizer APO yourself first (GamerTool detects and skips re-installing).
- Multi-device setups: auto-registration only targets whatever is the
  *default* playback device at the moment "Enable Audio EQ" is clicked. If
  you switch default devices later, click the button again (it's a no-op if
  that device is already registered).
- No code-signing on GamerTool.exe itself — Windows SmartScreen will warn on
  first run of an unsigned exe from an unknown publisher. That's expected
  for a self-published tool and isn't a bug.
- `UnregisterEfxForEndpoint()` exists as a clean-removal API but isn't wired
  to a UI button yet — worth adding an "Uninstall Audio Engine" action if
  you want a full undo path from within the app.
