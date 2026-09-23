# Gamer Tool

A portable Windows utility combining a driverless display-tuning engine
(brightness/contrast/gamma/black-equalizer/RGB) with a system-wide 10-band
audio EQ (via Equalizer APO), global hotkeys, and combo presets — built as a
single self-contained `.exe` with no traditional installer.

## Honest limitations (read this)

**There is no pure user-mode way** to apply system-wide EQ on Windows 10/11
without either:

- An **Audio Processing Object (APO)** loaded into `audiodg.exe` (what this
  app uses via Equalizer APO), or
- A **signed virtual audio driver** (what FxSound uses).

Microsoft requires the one-time admin step for APO registration. This app
makes that step as seamless as possible: one UAC prompt, auto-download of
Equalizer APO, auto-registration of your default playback device, and automatic
wiring of the config file.

Equalizer APO still works on Windows 11 24H2 (confirmed working as of
September 2026). The common failure modes are ACLs, missing
`DisableProtectedAudioDG`, and Windows updates detaching the APO — all of
which this build addresses.

## Why this won't mute your audio like earlier attempts

The classic failure happens because `audiodg.exe` runs as
`NT AUTHORITY\LOCAL SERVICE`. If the EQ config lives under `%USERPROFILE%` or
`%APPDATA%`, LOCAL SERVICE gets Access Denied → the APO faults → Windows
mutes the endpoint.

This build fixes that at the root:

- EQ config always lives at `C:\ProgramData\GamerTool\EQ\config.txt`.
- On first "Enable Audio EQ", GamerTool does a **one-time elevated setup**
  that:
  1. Grants `NT SERVICE\Audiosrv` and `LOCAL SERVICE` read/execute on that
     folder via `icacls`.
  2. Sets `DisableProtectedAudioDG=1` (required for unsigned APOs).
  3. Downloads + silently installs Equalizer APO if missing.
  4. Registers the APO on the current default playback device.
  5. Writes an `Include:` line into Equalizer APO's own `config.txt` so
     live slider changes take effect.
- The main app always runs as a normal user. It only re-launches itself
  elevated for that one setup step (and briefly for Panic Reset).
- Config writes go through a temp-file + replace so the file watcher never
  sees a half-written file.

Display tuning needs **no admin at all** — pure user-mode GDI
(`SetDeviceGammaRamp`). The original gamma ramp is captured at startup and
restored on exit / Reset.

## How to use

1. Launch `GamerTool.exe`.
2. Display tab works immediately.
3. On the Audio tab click **Enable Audio EQ** (one UAC prompt).
4. After setup completes, move the 10-band sliders — changes apply
   system-wide within ~1 second.
5. Use **Panic / Reset Audio** if anything ever goes wrong (writes flat
   config + restarts the Windows Audio service).

## Build

```powershell
dotnet publish GamerTool/GamerTool.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o ./publish
```

Or push to `main` — `.github/workflows/build.yml` builds on `windows-latest`
and uploads `GamerTool.exe` as an artifact.

## Project layout

```
GamerTool/
├── GamerTool.csproj
├── App.xaml / App.xaml.cs          routes elevated helper flags, captures original gamma
├── MainWindow.xaml(.cs)            Display / Audio / Hotkeys tabs + restore gamma on close
├── Models/
├── Services/
│   ├── DisplayService.cs           GDI gamma ramp + black-equalizer + original-ramp restore
│   ├── FocusWatcher.cs             re-asserts gamma for fullscreen-exclusive games
│   ├── AudioService.cs             config writer, ACLs, Include wiring, elevated entry points
│   ├── EqualizerApoInstallerService.cs  download/install/register + DisableProtectedAudioDG
│   ├── RegistryOwnershipHelper.cs  TrustedInstaller ownership takeover for MMDevices
│   ├── AudioDeviceService.cs       WASAPI endpoint enumeration
│   ├── HotkeyService.cs
│   └── ProfileManager.cs
└── UI/
```

## Known gaps

- Multi-device: auto-registration targets the *default* playback device at the
  moment you click Enable. Switch default later → click Enable again.
- No code-signing on GamerTool.exe → SmartScreen warning on first run (normal
  for self-published tools).
- After major Windows feature updates the APO can detach. Click Enable again
  or open Configurator and re-tick the device.
- An "Uninstall Audio Engine" UI button is not yet wired (the API exists:
  `EqualizerApoInstallerService.UnregisterEfxForEndpoint`).
