# 🎮 Gamer Tool

**Switch your screen and sound presets instantly with a single key.**

Boost shadow brightness to spot enemies in dark games, switch to a warm night mode, or enhance footsteps in your headset—all with one hotkey or automatically when a game launches.

[📥 **Download Latest Release (GamerTool.exe)**](https://github.com/MyLittlePrimordia/Gamer-Tool/releases/latest)  
*Zero install. Just download and run.*

---

## 📸 Screenshots

<table>
  <tr>
    <td width="50%"><img src="screenshots/display.png" alt="Display tab"></td>
    <td width="50%"><img src="screenshots/audio.png" alt="Audio tab"></td>
  </tr>
  <tr>
    <td width="50%"><img src="screenshots/crosshair.png" alt="Crosshair tab"></td>
    <td width="50%"><img src="screenshots/hotkeys.png" alt="Hotkeys tab"></td>
  </tr>
  <tr>
    <td width="50%"><img src="screenshots/settings.png" alt="Settings tab"></td>
    <td width="50%"></td>
  </tr>
</table>

---

## ⚡ What it does

- 🖥️ **Display Presets:** Instant profiles like *Competitive*, *Cinema*, *Daylight*, and *Night*, plus custom brightness, contrast, and color dials.
- 🎧 **Audio Presets & EQ:** Sound profiles and a multi-band equalizer (via free [FxSound](https://www.fxsound.com/)) with automatic distortion protection.
- ⌨️ **One-Key Switching:** Link a screen preset + a sound preset to a single hotkey (e.g. `Ctrl+Shift+1`) or have it turn on automatically when a game starts.
- ▶️ **Play With Profile:** A slot's Play button loads that slot - screen, sound, Windows audio routing, HDR - *before* it starts the game, then puts everything back when you exit. A game from Steam or Epic is started by its own launcher, so it behaves exactly as it does from the store; a shortcut can do the same from outside the window.
- 🎯 **Crosshair Overlay:** Eight minimal shapes in six high-contrast colors (or any color from the built-in picker), plus a browsable pack of ready-made crosshair PNGs. Size, thickness, gap, opacity, rotation, outline, glow and an optional center dot all work on shapes *and* pictures. Pick one per slot, or toggle and cycle them with their own hotkeys.
- 🪶 **Lightweight & Clean:** No accounts, no background telemetry, no installer. Lives quietly in your system tray, where you can toggle or switch your crosshair without opening the window.

## 🎯 Crosshair notes

- It is a small click-through window that only draws pixels. It never touches the game, injects anything or reads game memory, and it uses no CPU while sitting on screen.
- It shows over **borderless** and **windowed** games. A game in true **exclusive fullscreen** hides it, so switch the game to borderless.
- Some tournaments and leagues ban any overlay. Turn it off for those.
- **Add your own crosshair PNGs:** click **Browse pack...** on the Crosshair tab, then **Open folder**, and drop any PNG files (any file names) into it. The folder is `%AppData%\GamerTool\crosshair-pack` (beside the program in portable mode) and is created on first launch from the pictures built into the exe. It is read each time the browser opens, so new files just appear. In the source tree the starter pictures live in `app\Assets\crosshairs` and are embedded at build time.
- White-on-transparent pictures work best, because the color slider tints them. Thickness above 2 makes a picture's lines bolder, and outline, glow, opacity, size and rotation apply to it too.
- **Import PNG...** is for a one-off file. Imports are PNG only, checked, shrunk to 256 px and re-saved by the app.
- **Export PNG** saves the current crosshair as a transparent picture at its real size.
- The crosshair appears on the monitor that holds the foreground (game) window. Screen recording will include it.

---

## 🚀 Quick Start (3 Steps)

1. **Download & Run:** Grab [`GamerTool.exe`](https://github.com/MyLittlePrimordia/Gamer-Tool/releases/latest) and double-click it.
2. *(Optional)* **Enable Audio:** If you want audio presets, install [FxSound](https://www.fxsound.com/) (free—there's a direct button on the Settings tab).
3. **Set your Hotkey:** 
   - Open the **Hotkeys** tab and click **+**.
   - Pick your screen preset, sound preset, and hotkey.
   - Press that key in-game to toggle your settings!

---

## 📋 Requirements

- **Windows 10 or 11**
- [FxSound](https://www.fxsound.com/) *(free, only needed if you want the Audio tab)*

---

<details>
<summary><b>🛠️ Advanced Notes & Hardware Brightness</b></summary>

- **Verify setup:** the Settings row of that name tries brightness, HDR and Windows sound routing on this PC, puts every one of them back, and lists what answered. If a feature does nothing on your machine, that list is the first thing to paste into a report.
- **Hardware Brightness (DDC/CI):** Off by default. Some monitors support changing real backlight levels directly over the display cable. If a monitor fails to respond twice, Gamer Tool disables it safely to prevent freezing.
- **Settings Location:** Everything is stored locally in `%APPDATA%\GamerTool`. Copy this folder if you want to move your setup to a new PC.
- **Portable mode:** Put a `settings.json` next to `GamerTool.exe` and the whole setup moves into that folder — settings *and* the log. Handy for a USB stick. Leave the file out and nothing changes.
- **Accessibility:** Full keyboard navigation is supported. Every dial and control can be navigated using <kbd>Tab</kbd>, arrow keys, <kbd>Page Up</kbd>/<kbd>Down</kbd>, and <kbd>Esc</kbd>.
</details>

<details>
<summary><b>💻 Building from Source</b></summary>

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet publish app/GamerTool.csproj -c Release -o publish
```

This outputs a standalone `GamerTool.exe` in the `publish` folder.
</details>