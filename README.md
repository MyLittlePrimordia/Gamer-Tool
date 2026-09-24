# Gamer Tool

A portable Windows utility combining a driverless display-tuning engine
(brightness/contrast/gamma/black-equalizer/RGB) with a system-wide 10-band
audio EQ, global hotkeys, and combo presets — built as a single
self-contained `.exe` with no installer.

The audio EQ runs as a **Bridge**: a WASAPI loopback-capture → in-process
10-band EQ → WASAPI-render pipeline, routed through a small virtual audio
device (VB-CABLE) rather than hooking into Windows' protected audio engine
process. See "Audio engine architecture" below for why, and for the safety
model that keeps a crash from ever leaving your audio silently stuck.

## History: why this isn't Equalizer APO

The original plan for this project used Equalizer APO (a config file read by
an unsigned DLL hosted inside `audiodg.exe`, the Windows audio engine
process). Two separate problems killed that approach:

1. **The original mute bug**: `audiodg.exe` runs as `NT AUTHORITY\LOCAL
   SERVICE`. If the EQ config file lives somewhere LOCAL SERVICE can't read
   (`%APPDATA%`/`%USERPROFILE%`), the APO faults on its real-time callback
   and Windows mutes the device as a failsafe. This is fixable (see the
   legacy code's `C:\ProgramData\...` + `icacls` approach below) — but:
2. **Current Windows 11 blocks it entirely.** Fully-patched Windows 11 only
   loads digitally-signed APOs into `audiodg.exe`. Equalizer APO's DLL is
   unsigned, and the old `DisableProtectedAudioDG` registry bypass no longer
   works on recent builds. No amount of correct registry automation on our
   end changes that — it's a Microsoft security change, not a permissions bug.

That's why the audio engine is now the Bridge described below instead. The
original Equalizer APO implementation is kept in the codebase as dormant
reference code (see "Legacy path" below) since it's still valid on Windows
versions where the signing block doesn't apply.

Display tuning needs **no admin at all** — it's pure user-mode GDI
(`SetDeviceGammaRamp`), so that tab works immediately on a fresh install.

## Audio engine architecture: the Bridge (current, default)

**Recap of why:** current, fully-patched
Windows 11 only loads digitally-signed APOs into `audiodg.exe`. Equalizer APO's
DLL is unsigned, and the old `DisableProtectedAudioDG` bypass no longer works
on recent builds — so the APO-hook approach documented further down can
silently do *nothing audible* even when every registry step "succeeds." The
Bridge sidesteps that entirely by never hooking into `audiodg.exe` at all.

**How it works:**

```
apps/games --> "CABLE Input" (virtual device, set as Windows default)
                    |
                    | WASAPI loopback capture (AudioBridgeService)
                    v
              10-band EQ, in-process (GraphicEqProcessor, NAudio BiQuadFilter)
                    v
              WASAPI render --> your real speakers/headphones
```

A naive "loopback-capture the default device, EQ it, play it back to the same
device" pipeline would produce an **echo**, not an equalized stream — loopback
capture taps a *copy* of what's already playing, it doesn't remove the
original. Routing apps through an intermediate virtual device and rendering
the processed result to the real device is what avoids that: only one
(processed) copy of the audio ever reaches your ears.

**The virtual device** comes from [VB-CABLE](https://vb-audio.com/Cable/), a
free, already WHQL-signed virtual audio driver — so nothing in this pipeline
needs signing of our own. `Services/VirtualCableInstallerService.cs` downloads
it, verifies it against a pinned SHA-256 checksum (aborting rather than
running an unverified installer if it doesn't match), and silently installs
it (`-i -h`) elevated. **Note:** VB-Audio's own documentation says a reboot
can be required before the new device is fully registered — if "Enable Audio
EQ" reports that, restart and click it again.

**Setting the default device** is done via the `AudioSwitcher.AudioApi.CoreAudio`
NuGet package rather than hand-rolled COM interop
(`Services/AudioBridge/DefaultDeviceService.cs`) — the underlying Windows
mechanism (`IPolicyConfig`) is an undocumented COM interface whose vtable
layout differs across Windows versions, and guessing it wrong risks a hard
crash rather than a clean failure.

**Safety model — this is the part worth reading carefully.** If GamerTool
exits (or crashes) while Windows' default output is still pointed at the
virtual cable and nothing is running to bridge it onward, your audio goes
**silent system-wide** — the same class of failure the original Equalizer APO
plan was trying to avoid, just via a different mechanism. Two things guard
against that:
- `MainWindow_Closing` always stops the bridge and restores your real device
  on every normal shutdown path (window close, Panic button, hotkey panic).
- A **startup self-heal check** in `MainWindow_Loaded`: a `BridgeActive` flag
  in settings is only ever `true` while the bridge is deliberately running,
  and is cleared on every clean shutdown. If it's still `true` on the *next*
  launch, GamerTool didn't exit cleanly last time (crash, task kill, power
  loss) — so startup immediately restores the real device before you'd
  otherwise notice silence.

**Known limitation:** shared-mode WASAPI render relies on Windows' own
sample-rate/format auto-conversion between the virtual cable and your real
device. This covers the overwhelming majority of setups (both normally
converge on 48kHz float stereo). If you hit a device with an unusual native
format, the fix is inserting a `NAudio.Wave.MediaFoundationResampler` between
capture and render in `AudioBridgeService.Start()` — not currently wired in,
since it'd be guessing at a problem rather than fixing an observed one.

## Legacy path: Equalizer APO (dormant, not used by default)

`Services/AudioService.cs`, `Services/EqualizerApoInstallerService.cs`, and
`Services/RegistryOwnershipHelper.cs` still contain a full, working
implementation of the original plan (system-wide EQ via an Equalizer APO
config file + automated device registration). It's no longer wired to the UI
— nothing calls it — because of the signing issue above. It's left in place
as reference/fallback code: if Microsoft's signing enforcement changes, or if
you're on a Windows version where the old bypass still works, this path can
be re-wired to `MainWindow`'s audio handlers in place of the Bridge calls.

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
│   ├── AudioBridge/
│   │   ├── AudioBridgeService.cs   capture/EQ/render pipeline (the current default audio engine)
│   │   ├── GraphicEqProcessor.cs   live 10-band peaking EQ (NAudio BiQuadFilter)
│   │   └── DefaultDeviceService.cs default-playback-device switching (AudioSwitcher)
│   ├── VirtualCableInstallerService.cs  downloads/verifies/installs VB-CABLE
│   ├── AudioDeviceService.cs WASAPI render-endpoint enumeration (COM interop), used by the output-device dropdown
│   ├── HotkeyService.cs     Win32 RegisterHotKey wrapper
│   ├── ProfileManager.cs    JSON persistence under %AppData%\GamerTool
│   ├── AudioService.cs, EqualizerApoInstallerService.cs, RegistryOwnershipHelper.cs
│   │     legacy Equalizer-APO engine — not wired to the UI, see "Legacy path" above
└── UI/
    ├── OsdNotification.xaml(.cs)     click-through in-game HUD toast
    ├── HotkeyCaptureWindow.xaml(.cs) key-combo recorder
    ├── InputDialog.xaml(.cs)         name-entry modal
    ├── CustomSliders.xaml            EQ band + HUD slider styles
    └── Converters.cs
```

## Known gaps / next steps

- **VB-CABLE checksum staleness**: the download is pinned to a specific,
  checksum-verified release (`VBCABLE_Driver_Pack43.zip`). If VB-Audio ships
  a newer pack, GamerTool will refuse to run the new (unverified) file rather
  than install it blind — you'd need to update the pinned URL/hash in
  `VirtualCableInstallerService.cs`, or just install VB-CABLE yourself once
  (GamerTool detects and skips re-installing).
- **Reboot-after-install**: VB-Audio's docs say a reboot can be required
  before the new device is fully registered. GamerTool detects this case and
  tells you, rather than silently failing.
- **NAudio pinned to 2.2.1**: NAudio 3.x is still in preview and has
  obsoleted the exact APIs this project uses (`WasapiLoopbackCapture`,
  `WasapiOut`) in favor of new `WasapiRecorder`/`WasapiPlayer` types. 2.2.1 is
  the stable, non-moving target — worth revisiting once 3.x stabilizes.
- **No resampler wired in**: shared-mode WASAPI handles format conversion
  between the virtual cable and your real device automatically in the
  common case (both usually run 48kHz float stereo). An unusual device would
  need a `MediaFoundationResampler` inserted in `AudioBridgeService.Start()`.
- No code-signing on GamerTool.exe itself — Windows SmartScreen will warn on
  first run of an unsigned exe from an unknown publisher. That's expected
  for a self-published tool and isn't a bug.
- The `AudioSwitcher.AudioApi.CoreAudio` device-switching calls
  (`Services/AudioBridge/DefaultDeviceService.cs`) are built against the
  library's documented/observed API surface but weren't compiled against a
  live copy of the package — if a method name has shifted in 3.0.3, that's
  the first place to check a build error against.
