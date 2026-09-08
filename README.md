# Gamer Tool

A free, single-file Windows utility for tweaking display gamma/contrast and
in-game audio EQ on the fly, with global hotkeys that work even while a game
has exclusive fullscreen focus. No installer, no background services beyond
the app itself, no third-party runtime dependencies - just one `.exe` that
sits in your system tray.

Think of it as a lightweight, open alternative to the color/audio panels
bundled with gaming peripherals (Razer Synapse, Logitech G HUB), minus the
account login, the extra background processes, and the hardware lock-in.

## What it does

**Display**
- Adjusts gamma, contrast, shadow lift, brightness, and per-channel RGB gain
  by writing directly to your monitor's hardware gamma ramp (the same
  mechanism f.lux and Windows Night Light use) - no overlay, no GPU shader,
  no compatibility issues with anti-cheat.
- 7 built-in presets: Default, Dark Scenes, Footstep / Enemy Spotter,
  Cinematic & Story, Vibrant World, Night Eye Comfort, and Bright Room /
  Sunlight.
- A live preview box shows the effect on a sample dark scene before (and as)
  you commit to it.
- Whatever you had before Gamer Tool touched your display is always
  recoverable - see **Safety net**, below.

**Audio**
- A 10-band graphic EQ (31 Hz - 16 kHz), color-coded into six gamer-labeled
  frequency zones (Sub-Bass through Treble) so it's obvious which slider
  affects footsteps versus explosions versus voice chat.
- If [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) is
  installed, Gamer Tool drives it directly for true parametric EQ. If it
  isn't, Gamer Tool falls back to toggling Windows' own native "Loudness
  Equalization" endpoint enhancement.
- 7 built-in presets: Default, Footsteps & Movement, Explosion Damper, Late
  Night (Quiet Mode), Dialogue & Voice, Heavy Bass & Rumble, and Crisp
  Treble.

**Hotkeys & Combos**
- Every preset (Display or Audio) can be bound to its own global hotkey,
  recorded right in the app, with live conflict detection against hotkeys
  already claimed by other running applications.
- "Combos" pair one Display preset with one Audio preset behind a single
  hotkey, so e.g. "Competitive Mode" can flip both your monitor and your
  headset in one keystroke.
- **Emergency Reset** (`Ctrl + Alt + R`) instantly restores factory display
  gamma and clears any active audio EQ. It's always active - even while
  Gamer Tool is minimized to the tray - and it isn't reassignable.

**Runs in the background**
- Minimizes to the tray, not the taskbar. Near-zero CPU and well under
  25 MB of RAM at idle.
- Optional "Run on Windows Startup" toggle (adds a per-user, no-admin-needed
  entry to `HKCU\...\Run` - nothing system-wide, nothing that needs
  elevation).

## Safety net

Gamma ramps are a property of your display driver, not of Gamer Tool's
process - so if Gamer Tool ever crashed while a custom ramp was active and
did nothing about it, your desktop would stay color-shifted until something
else fixed it. Gamer Tool is built around not letting that happen:

- Your factory gamma ramp is captured the moment Gamer Tool starts, before
  any preset can touch it, and backed up to
  `%LocalAppData%\GamerTool\factory_ramp.bin`.
- A restore is wired to *every* way the process can end: normal exit,
  unhandled exceptions on any thread, Windows shutdown/logoff/restart, and a
  few defense-in-depth edge cases besides.
- If Gamer Tool is ever killed hard enough to skip all of that (e.g. Task
  Manager "End Task" mid-crash), it detects the dirty session on next launch
  and restores your factory ramp automatically before doing anything else.
- Diagnostic notes about when/why a restore fired are logged to
  `%LocalAppData%\GamerTool\diagnostics.log`, purely for troubleshooting.

## Installing

Grab `GamerTool.exe` from the
[latest release](../../releases/tag/latest) - it's a single, self-contained
file. No installer, no admin rights required. Run it and it lands in your
system tray.

Supported on Windows 10 and Windows 11 (Home, Pro, Enterprise, and LTSC).

## Building from source

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
on Windows.

```powershell
git clone https://github.com/<your-org>/gamer-tool.git
cd gamer-tool
dotnet publish GamerTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The published `GamerTool.exe` lands in
`bin\Release\net8.0-windows\win-x64\publish\`. The GitHub Actions workflow in
`.github/workflows/build.yml` runs this exact command on every push to `main`
and republishes the result as the "latest" release.

There is nothing to `dotnet restore` from NuGet - every native capability
(gamma ramps, global hotkeys, the tray icon, CoreAudio, the registry) is
reached through raw P/Invoke and COM interop in `src/Core/NativeMethods.cs`,
not third-party wrapper packages.

## A couple of honest caveats

- **Native "Loudness Equalization" toggling relies on an undocumented
  per-endpoint driver property.** It's not a stable, published Microsoft
  contract, and it can simply be unsupported on some audio drivers. When
  that happens, Gamer Tool degrades gracefully - the EQ bands still work via
  Equalizer APO if you have it installed, and the toggle itself just reports
  "unavailable" rather than doing something wrong silently.
- **Two copies of Gamer Tool won't run side by side.** A single-instance
  check prevents a second launch from fighting the first one over the same
  gamma ramp and hotkeys; right now a duplicate launch just quietly exits
  rather than bringing the first window to the front (a nicer version of
  that hand-off is on the list).
- **The in-app preview scene is drawn procedurally**, not loaded from
  artwork - so it's a simple placeholder dark room and silhouette rather
  than a polished game screenshot. It reflects your actual gamma math
  pixel-for-pixel, just with programmer art.

## License

Add your preferred license here before publishing (e.g. MIT).
