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
    <td width="50%"><img src="screenshots/hotkeys.png" alt="Hotkeys tab"></td>
    <td width="50%"><img src="screenshots/settings.png" alt="Settings tab"></td>
  </tr>
</table>

---

## ⚡ What it does

- 🖥️ **Display Presets:** Instant profiles like *Competitive*, *Cinema*, *Daylight*, and *Night*, plus custom brightness, contrast, and color dials.
- 🎧 **Audio Presets & EQ:** Sound profiles and a multi-band equalizer (via free [FxSound](https://www.fxsound.com/)) with automatic distortion protection.
- ⌨️ **One-Key Switching:** Link a screen preset + a sound preset to a single hotkey (e.g. `Ctrl+Shift+1`) or have it turn on automatically when a game starts.
- 🪶 **Lightweight & Clean:** No accounts, no background telemetry, no installer. Lives quietly in your system tray.

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

- **Hardware Brightness (DDC/CI):** Off by default. Some monitors support changing real backlight levels directly over the display cable. If a monitor fails to respond twice, Gamer Tool disables it safely to prevent freezing.
- **Settings Location:** Everything is stored locally in `%APPDATA%\GamerTool`. Copy this folder if you want to move your setup to a new PC.
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