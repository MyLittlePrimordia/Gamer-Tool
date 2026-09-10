# Gamer Tool

A zero-dependency, single-file Windows utility for gamers: display presets,
system-wide audio EQ presets, one-hotkey combos, and global hotkeys - built
as one standalone `GamerTool.exe` you can place anywhere and run.

## What's inside

- **Display presets** - gamma / contrast / shadow-lift / RGB gain via the
  GDI gamma ramp (restored automatically on exit, crash, or panic hotkey)
- **Audio presets** - 10-band system-wide EQ powered by GamerTool's own
  native APO (`GamerToolAPO.dll`) that runs inside the Windows audio engine
  (audiodg.exe). No external apps to install.
- **Combos** - pair any Display + Audio preset behind one master hotkey
- **Global hotkeys** - per-preset and per-combo bindings that work while a
  game has exclusive fullscreen focus
- **Tray-first** - close-to-tray, tray menu, optional run-on-startup
- **Crash safety** - factory gamma is captured at launch and restored by
  watchdog hooks on any exit path; panic hotkey `Ctrl+Alt+R` always works

## How the built-in EQ works

Windows processes all system audio in `audiodg.exe`, which only loads
registered Audio Processing Objects (APOs). Gamer Tool ships its own tiny
APO (10 peaking biquads, one per ISO band) and embeds it inside the exe:

1. First run: click **Enable Built-in EQ** (one UAC prompt)
2. GamerTool extracts the APO to `%ProgramData%\GamerTool\GamerToolAPO.dll`,
   registers it, wires it into every active playback device, and restarts
   the audio service
3. From then on, preset changes publish to a shared-memory section the APO
   reads on the real-time audio thread - instant, system-wide, zero polling

To uninstall the engine: Settings -> Built-in Equalizer -> Disable (also a
one-time UAC prompt). The endpoint's original APO chain is restored.

> Note: the APO is unsigned (no EV code-signing certificate), so enablement
> also sets the Windows `DisableProtectedAudioDG` flag - the same mechanism
> Equalizer APO uses. Antivirus software may ask you to trust GamerTool once.

## Building

Requires .NET 8 SDK; the native APO needs MSVC (VS 2022 Build Tools with the
C++ workload and Windows SDK) - GitHub Actions builds both automatically
via `.github/workflows/build.yml`.

Local build with full engine:

```powershell
msbuild app\src\Native\GamerToolAPO\GamerToolAPO.vcxproj /p:Configuration=Release /p:Platform=x64
New-Item -ItemType Directory -Force app\src\Native\GamerToolAPO\prebuilt
Copy-Item app\src\Native\GamerToolAPO\x64\Release\GamerToolAPO.dll app\src\Native\GamerToolAPO\prebuilt\
dotnet publish app\GamerTool.csproj -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

UI-only development without the C++ toolchain:

```powershell
dotnet build app\GamerTool.csproj -p:EmbedNativeApo=false
```

## Repository layout

```
.github/workflows/build.yml   CI: builds native APO + single-file exe, releases
app/GamerTool.csproj         the app project (WPF, net8.0-windows)
app/src/                     C# source (Core/, Models/, ViewModels/, Services/)
app/src/Native/GamerToolAPO/ native C++ APO source (MSVC vcxproj)
app/Assets/                  icons, tray glyphs, preview scenes
```

## License

GPL-2.0-or-later for the APO (biquad/COM structure informed by
EqualizerAPO by Jonas Thedering). The C# app is MIT.
