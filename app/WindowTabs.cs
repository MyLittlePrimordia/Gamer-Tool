using System;

namespace GamerTool;

/// <summary>
/// Which tabs a window brings up, for the headless constructor used by the test
/// harness.
/// <para>
/// The real constructor brings up every tab, wires the tray, registers the global
/// hotkeys and starts the audio engine. A test needs one tab's controls live and
/// nothing else, both so that it does not touch the machine and so that a failure
/// can only be about the tab under test. <see cref="All"/> is what the app itself
/// uses, through the public constructor.
/// </para>
/// </summary>
[Flags]
internal enum MainWindowTabs
{
    /// <summary>The window's markup, and nothing initialised.</summary>
    None = 0,

    /// <summary>The Display tab: the preset box, the sliders and the scene preview.</summary>
    Display = 1 << 0,

    /// <summary>The Audio tab: the preset box, the faders, the preview transport.</summary>
    Audio = 1 << 1,

    /// <summary>The Hotkeys tab: the slot board and its cards.</summary>
    Slots = 1 << 2,

    /// <summary>The Crosshair tab: the shape tiles, the colour row and the pack browser.</summary>
    Crosshair = 1 << 3,

    /// <summary>Every tab, which is what the shipping window does.</summary>
    All = Display | Audio | Slots | Crosshair
}
