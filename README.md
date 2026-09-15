# Gamer Tool

A single-file Windows utility for gamers: display presets, system-wide audio
EQ presets, one-hotkey combos, and global hotkeys - `GamerTool.exe`, place it
anywhere and run.

## What's inside

- **Display presets** - gamma / contrast / shadow-lift / RGB gain via the
  GDI gamma ramp (restored automatically on exit, crash, or panic hotkey)
- **Audio presets** - 10-band system-wide EQ (see below)
- **Combos** - pair any Display + Audio preset behind one master hotkey,
  with optional auto-activate when a chosen game/app is in focus
- **Global hotkeys** - per-preset and per-combo bindings that work while a
  game has exclusive fullscreen focus
- **Tray-first** - close-to-tray, tray menu, optional run-on-startup
- **Crash safety** - factory gamma is captured at launch and restored by
  watchdog hooks on any exit path; panic hotkey `Ctrl+Alt+R` always works

## How the audio EQ works

Windows only lets system-wide audio effects run as registered, trusted Audio
Processing Objects inside `audiodg.exe`. A from-scratch, unsigned APO is
silently refused by Windows on most modern PCs (Memory Integrity / Core
Isolation, on by default since Windows 11) - no error, it just never loads.

Rather than fight that wall, GamerTool is a friendly front-end over
[EqualizerAPO](https://sourceforge.net/projects/equalizerapo/) (GPL-2.0, by
Jonas Thedering) - a properly signed, decade-proven APO that already solved
this exact problem:

1. Settings -> Equalizer Engine -> **Set Up** (downloads and opens the
   official EqualizerAPO installer - a normal admin install, one time)
2. GamerTool detects it automatically once installed and writes its own
   `GamerTool.txt` config file (`Preamp:` + `GraphicEQ:` lines) next to
   EqualizerAPO's `config.txt`, which it includes without touching anything
   else you might already have configured there
3. EqualizerAPO watches that file and hot-reloads within a fraction of a
   second of any change - no polling, no elevation, no native code of our own

Once EqualizerAPO is installed, GamerTool never needs administrator rights
for anything - EqualizerAPO's own installer grants the Windows "Users" group
write access to its config folder specifically, for exactly this purpose.

The "Balance Loud & Quiet Sounds" toggle uses Windows' own per-endpoint
Loudness Equalization enhancement instead - EqualizerAPO is EQ-only and has
no dynamics/compression support, and Windows already ships a real one.

## Building

Requires only the .NET 8 SDK - there is no native/C++ project to build.

```powershell
dotnet publish app\GamerTool.csproj -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Repository layout

```
.github/workflows/build.yml   CI: single-file exe publish + release
app/GamerTool.csproj          the app project (WPF, net8.0-windows)
app/src/                      C# source (Core/, Models/, ViewModels/)
app/Assets/                   icons, tray glyphs, preview scenes
```

## License

MIT. GamerTool does not bundle or modify EqualizerAPO's GPL-licensed code -
it detects an install and writes plain text config files it reads, the same
way a user editing `config.txt` by hand would.
