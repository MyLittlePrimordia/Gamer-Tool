using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GamerTool.Services;

/// <summary>
/// One slot, as the tray menu needs to describe it.
/// <para>
/// The tray is handed text and an id rather than a <c>HotkeySlot</c>. It is a
/// WinForms menu with no knowledge of presets, monitors or profiles, and keeping
/// it that way is what stops a slot edit from needing a change in two places.
/// </para>
/// </summary>
public sealed class TraySlotEntry
{
    /// <summary>Which slot to load. Sent back on <see cref="TrayService.SlotRequested"/>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What the menu line says.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The hover text: the key, what it loads, which screen.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>True for the slot that matches what is loaded, which is ticked.</summary>
    public bool Active { get; set; }
}

public sealed class TrayService : IDisposable
{
    /// <summary>The app's name, on its own line of the tooltip.</summary>
    public const string Title = "Gamer Tool";

    /// <summary>
    /// The shell allows 128 characters for a tip including the terminator, so 127
    /// of text. It throws rather than truncating, hence the clamp.
    /// </summary>
    private const int MaxTipLength = 127;

    private readonly NotifyIcon _icon;

    private readonly ContextMenuStrip _menu;

    /// <summary>
    /// The line that opens the slot list, and the rule above it.
    /// <para>
    /// Slots sit behind an arrow rather than in the menu itself. Six of them is a
    /// tidy list; a user who makes their own can have twenty, and a menu that
    /// long stops being a menu - it either runs off the bottom of the screen or
    /// pushes Reset and Quit somewhere they are no longer in the same place.
    /// Behind an arrow the menu is a fixed six lines however many slots exist,
    /// and the list that can grow is the one the shell already knows how to
    /// scroll.
    /// </para>
    /// </summary>
    private readonly ToolStripMenuItem _slotsItem;

    private readonly ToolStripSeparator _slotsSeparator;

    /// <summary>
    /// One step back from the preset the user loaded last.
    /// <para>
    /// Kept as a field rather than rebuilt with the slot lines because it is a
    /// single fixed line, and because its enabled state is pushed from the window
    /// as the undo becomes available and is spent.
    /// </para>
    /// </summary>
    private readonly ToolStripMenuItem _undoItem;

    /// <summary>
    /// The Crosshair line and the list behind it: show/hide, next, then the
    /// presets. Behind an arrow for the same reason as the slots - the preset list
    /// is user-grown, and the main menu stays a fixed length.
    /// </summary>
    private readonly ToolStripMenuItem _crosshairItem;

    private bool _disposed;

    public event Action? ShowRequested;

    public event Action? ResetScreenRequested;

    public event Action? ResetSoundRequested;

    /// <summary>Raised with a slot id. The id, not the index, because the menu is rebuilt.</summary>
    public event Action<string>? SlotRequested;

    /// <summary>Raised when the user picks Undo.</summary>
    public event Action? UndoRequested;

    /// <summary>Raised when the user picks Show / Hide crosshair.</summary>
    public event Action? CrosshairToggleRequested;

    /// <summary>Raised when the user picks Next crosshair.</summary>
    public event Action? CrosshairNextRequested;

    /// <summary>Raised with a crosshair preset id when the user picks one from the list.</summary>
    public event Action<string>? CrosshairPresetRequested;

    public event Action? QuitRequested;

    public TrayService()
    {
        ToolStripMenuItem show = DarkTrayMenuRenderer.Item("Open", (s, e) => ShowRequested?.Invoke());
        ToolStripMenuItem resetScreen = DarkTrayMenuRenderer.Item("Reset Display", (s, e) => ResetScreenRequested?.Invoke());
        ToolStripMenuItem resetSound = DarkTrayMenuRenderer.Item("Reset Sound", (s, e) => ResetSoundRequested?.Invoke());
        ToolStripMenuItem quit = DarkTrayMenuRenderer.Item("Quit", (s, e) => QuitRequested?.Invoke());

        // Disabled rather than hidden when there is nothing to undo. A menu that
        // grows and shrinks a line moves Reset and Quit about under the cursor,
        // which is the same reason the slots separator is left standing when the
        // list is empty.
        _undoItem = DarkTrayMenuRenderer.Item(
            "Undo last load",
            (s, e) => UndoRequested?.Invoke());
        _undoItem.Enabled = false;
        _undoItem.ToolTipText = "Put back the preset that was loaded before the last one";

        _slotsItem = DarkTrayMenuRenderer.Submenu("Slots");
        _slotsSeparator = DarkTrayMenuRenderer.Separator();

        // CheckOnClick is off in CheckableItem, so the window owns the ticked state
        // and these only report that the user asked. The tooltip says which way round
        // the tick is, because a line called "Sound effects" with a tick beside it is
        // genuinely ambiguous and getting it backwards means muting when the user
        // asked to hear something.
        _effectsItem = DarkTrayMenuRenderer.CheckableItem(
            "Sound effects",
            "Tick means the effects are OFF. FxSound must be installed.",
            active: false,
            (s, e) => EffectsToggleRequested?.Invoke());

        _nightItem = DarkTrayMenuRenderer.CheckableItem(
            "Night filter",
            "Warm the screen on a schedule",
            active: false,
            (s, e) => NightToggleRequested?.Invoke());

        _crosshairItem = DarkTrayMenuRenderer.Submenu("Crosshair");
        // Seeded so the arrow opens onto something even if the menu is read
        // before the first state push; MenuOpening replaces it with the live list.
        SetCrosshairs(false, Array.Empty<TraySlotEntry>());

        _menu = DarkTrayMenuRenderer.Menu();
        _menu.Items.Add(show);
        _menu.Items.Add(_slotsSeparator);
        _menu.Items.Add(_slotsItem);
        _menu.Items.Add(DarkTrayMenuRenderer.Separator());
        _menu.Items.Add(_crosshairItem);
        _menu.Items.Add(_effectsItem);
        _menu.Items.Add(_nightItem);
        // U-003: second, clearly labelled power action beside the flatten
        // bypass. Off the default path on purpose: power-off switches the
        // device and games with open streams may notice.
        _menu.Items.Add(DarkTrayMenuRenderer.Item(
            "Power off FxSound",
            (s, e) => FxPowerOffRequested?.Invoke()));
        _menu.Items.Add(DarkTrayMenuRenderer.Item(
            "Power on FxSound",
            (s, e) => FxPowerOnRequested?.Invoke()));
        _menu.Items.Add(DarkTrayMenuRenderer.Separator());
        _menu.Items.Add(_undoItem);
        _menu.Items.Add(resetScreen);
        _menu.Items.Add(resetSound);
        _menu.Items.Add(DarkTrayMenuRenderer.Separator());
        _menu.Items.Add(quit);

        _icon = new NotifyIcon
        {
            Icon = IconFactory.CreateRuntimeIcon(32),
            Text = Title,
            Visible = true,
            // Menu is shown from MouseUp rather than ContextMenuStrip, so a
            // fullscreen game cannot keep the popup buried under its surface.
            ContextMenuStrip = null
        };

        _icon.DoubleClick += (s, e) => ShowRequested?.Invoke();
        _icon.MouseUp += OnTrayMouseUp;

        // Opening, not a state push from every place that changes it.
        //
        // The window owns both switches and there are five or six places that could
        // change them - the Audio tab, the Settings tab, the bypass key, a slot
        // applying, a profile restore. Pushing from each is how the tick and the
        // switch end up disagreeing, and the tick is the one the user trusts. The
        // menu can only be opened by the user, so reading the state as it is at that
        // moment is both cheaper and the only version that cannot drift.
        _menu.Opening += (s, e) =>
        {
            MenuOpening?.Invoke();
            BringMenuAboveFullscreen();
        };
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        // Show at the cursor after forcing foreground. Assigning ContextMenuStrip
        // to NotifyIcon lets the shell place the menu; under a borderless or
        // exclusive fullscreen game that often ends up behind the game window.
        BringMenuAboveFullscreen();
        _menu.Show(Control.MousePosition);
    }

    /// <summary>
    /// Make the tray menu the foreground window so it draws above a fullscreen game.
    /// <para>
    /// Other tray apps appear on top because their host process briefly becomes
    /// foreground when the menu opens. Without that step, a ContextMenuStrip from a
    /// background WPF process stays under the game's topmost surface.
    /// </para>
    /// </summary>
    private void BringMenuAboveFullscreen()
    {
        if (!_menu.IsHandleCreated)
        {
            _ = _menu.Handle;
        }

        IntPtr menuHwnd = _menu.Handle;
        if (menuHwnd == IntPtr.Zero)
        {
            return;
        }

        IntPtr foreground = GetForegroundWindow();
        uint menuThread = GetWindowThreadProcessId(menuHwnd, IntPtr.Zero);
        uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint thisThread = GetCurrentThreadId();

        // Attach to the foreground thread so SetForegroundWindow is allowed while
        // a game holds focus. Detach immediately after.
        bool attachedToFg = false;
        bool attachedToMenu = false;
        try
        {
            if (foreground != IntPtr.Zero && foregroundThread != thisThread)
            {
                attachedToFg = AttachThreadInput(thisThread, foregroundThread, true);
            }

            if (menuThread != thisThread && menuThread != 0)
            {
                attachedToMenu = AttachThreadInput(thisThread, menuThread, true);
            }

            BringWindowToTop(menuHwnd);
            SetForegroundWindow(menuHwnd);
        }
        finally
        {
            if (attachedToMenu)
            {
                AttachThreadInput(thisThread, menuThread, false);
            }

            if (attachedToFg)
            {
                AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// The two checkable lines, kept as fields because their ticked state is
    /// written from <see cref="SetToggleStates"/> rather than by the click.
    /// </summary>
    private readonly ToolStripMenuItem _effectsItem;
    private readonly ToolStripMenuItem _nightItem;

    /// <summary>
    /// Raised as the menu is about to show, so the window can push current state
    /// into it before anything is drawn.
    /// </summary>
    public event Action? MenuOpening;

    /// <summary>Raised when the user picks the sound effects line.</summary>
    public event Action? EffectsToggleRequested;

    /// <summary>Raised when the user picks the night filter line.</summary>
    public event Action? NightToggleRequested;

    /// <summary>Raised when the user picks Power off FxSound (U-003 opt-in).</summary>
    public event Action? FxPowerOffRequested;

    /// <summary>Raised when the user picks Power on FxSound (U-003 opt-in).</summary>
    public event Action? FxPowerOnRequested;

    /// <summary>
    /// Puts the current state into the two toggles.
    /// </summary>
    /// <param name="effectsOn">Whether the bypass is engaged, so the effects are off.</param>
    /// <param name="effectsAvailable">
    /// Whether the engine is installed. The line is hidden rather than disabled when
    /// it is not: a menu item that cannot work is a question the user has to ask the
    /// answer to.
    /// </param>
    /// <param name="nightOn">Whether the night schedule is switched on.</param>
    public void SetToggleStates(bool effectsOn, bool effectsAvailable, bool nightOn)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _effectsItem.Visible = effectsAvailable;
            _effectsItem.Checked = effectsOn;
            _nightItem.Checked = nightOn;
        }
        catch (Exception ex)
        {
            TraceLog.Write("TRAY", ex);
        }
    }

    /// <summary>
    /// Rebuilds the Crosshair list: Show/Hide, Next, then one ticked line per preset.
    /// <para>
    /// Rebuilt as the menu opens rather than pushed on every change, for the same
    /// reason the two toggles are: the window owns the state and the menu can only
    /// be opened by the user, so reading it at that moment cannot drift.
    /// </para>
    /// </summary>
    /// <param name="visible">Whether the crosshair is on screen, which flips the first line's wording.</param>
    /// <param name="presets">The presets, with the loaded one marked Active.</param>
    public void SetCrosshairs(bool visible, IReadOnlyList<TraySlotEntry> presets)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            ToolStripItemCollection target = _crosshairItem.DropDownItems;
            for (int i = target.Count - 1; i >= 0; i--)
            {
                ToolStripItem? stale = target[i];
                target.RemoveAt(i);
                stale?.Dispose();
            }

            target.Add(DarkTrayMenuRenderer.CheckableItem(
                visible ? "Hide crosshair" : "Show crosshair",
                "Put the crosshair on the screen, or take it off",
                visible,
                (s, e) => CrosshairToggleRequested?.Invoke()));

            target.Add(DarkTrayMenuRenderer.Item(
                "Next crosshair",
                (s, e) => CrosshairNextRequested?.Invoke()));

            if (presets.Count > 0)
            {
                target.Add(DarkTrayMenuRenderer.Separator());
                foreach (TraySlotEntry entry in presets)
                {
                    target.Add(DarkTrayMenuRenderer.CheckableItem(
                        entry.Label,
                        entry.Detail,
                        entry.Active,
                        (s, e) => CrosshairPresetRequested?.Invoke(entry.Id)));
                }
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("TRAY", ex);
        }
    }

    /// <summary>
    /// Enables or greys out the Undo line.
    /// <para>
    /// Pushed rather than read as the menu opens, unlike the two toggles above.
    /// Those two are answered by reading a switch the window already owns.
    /// "Is there a load worth taking back" is not a field anywhere: it is a
    /// decision the window makes as the load happens, at the moment it happens,
    /// and once the load has been superseded there is no way to reconstruct it
    /// from the current state. So the window says so when it changes.
    /// </para>
    /// </summary>
    public void SetUndoAvailable(bool available)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _undoItem.Enabled = available;
        }
        catch (Exception ex)
        {
            TraceLog.Write("TRAY", ex);
        }
    }

    /// <summary>
    /// A slot's menu line: the name, and the screen it is bound to when it is
    /// bound to one.
    /// <para>
    /// The monitor is on the line rather than only in the hover text because of
    /// the thing this menu is for. The tray is opened while looking at one
    /// screen and a slot can be pointed at another, and a list of six names that
    /// all look the same is a list where picking the wrong one is easy and the
    /// mistake is only visible on the monitor you are not facing. Six of these
    /// fit on a taskbar menu without truncation.
    /// </para>
    /// </summary>
    public static string ComposeSlotLabel(string? name, string? monitorName)
    {
        string clean = string.IsNullOrWhiteSpace(name) ? "Unnamed" : name.Trim();

        return string.IsNullOrWhiteSpace(monitorName)
            ? clean
            : clean + "  (" + monitorName.Trim() + ")";
    }

    /// <summary>
    /// A slot's hover text: the key it is on, what it loads, and which screen.
    /// <para>
    /// The parts that are not set are left out rather than shown as blanks, so a
    /// slot with no key does not read as a broken one.
    /// </para>
    /// </summary>
    public static string ComposeSlotDetail(string? hotkey, string? work, string? monitorName)
    {
        List<string> parts = new(3);

        if (!string.IsNullOrWhiteSpace(hotkey))
        {
            parts.Add(hotkey.Trim());
        }

        if (string.IsNullOrWhiteSpace(work))
        {
            parts.Add("Nothing set");
        }
        else
        {
            parts.Add(work.Trim());
        }

        if (!string.IsNullOrWhiteSpace(monitorName))
        {
            parts.Add(monitorName.Trim());
        }

        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// Replaces the slot lines in the menu.
    /// <para>
    /// Rebuilt rather than updated, because the things that change - a slot
    /// renamed, added, removed, or pointed at another screen - are the same
    /// number of TextBox and Checked writes as it is new items, and a fresh
    /// range cannot be left holding a line for a slot that no longer exists. The
    /// menu is only ever open while the user is looking at it, so the churn is
    /// never seen.
    /// </para>
    /// <para>
    /// The separator above the range is left in place even when there is nothing
    /// to put under it, because a menu that grows and shrinks as the slot list
    /// changes moves the Reset and Quit lines about under the cursor.
    /// </para>
    /// </summary>
    public void SetSlots(IReadOnlyList<TraySlotEntry> slots)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            ReplaceSlotLines(_slotsItem.DropDownItems, slots, id => SlotRequested?.Invoke(id));

            // Both the arrow and the rule above it go when there is nothing to
            // put under them. A menu with a bare "----" under Open, or a "Slots"
            // that opens onto nothing, is a line that is only taking up room
            // and asking a question the answer to is no.
            bool any = slots.Count > 0;
            _slotsItem.Visible = any;
            _slotsSeparator.Visible = any;
        }
        catch (Exception ex)
        {
            TraceLog.Write("TRAY", ex);
        }
    }

    /// <summary>
    /// Swaps the contents of <paramref name="target"/> for <paramref name="slots"/>.
    /// <para>
    /// Takes the collection rather than the strip and an index, so there is no
    /// position to get wrong. The first version of this spliced into the parent
    /// menu at a remembered index and counted what it had inserted, and an
    /// off-by-one in that index is invisible until somebody renames a slot and
    /// the menu grows a line for one that no longer exists. Emptying a
    /// collection and refilling it cannot go wrong in that way.
    /// </para>
    /// <para>
    /// Static and taking the collection as an argument, rather than reaching
    /// through the icon, so the swap can be tested without standing up a
    /// <see cref="NotifyIcon"/>.
    /// </para>
    /// </summary>
    internal static void ReplaceSlotLines(
        ToolStripItemCollection target,
        IReadOnlyList<TraySlotEntry> slots,
        Action<string> onSlot)
    {
        for (int i = target.Count - 1; i >= 0; i--)
        {
            ToolStripItem? stale = target[i];
            target.RemoveAt(i);
            stale?.Dispose();
        }

        foreach (TraySlotEntry entry in slots)
        {
            target.Add(DarkTrayMenuRenderer.CheckableItem(
                entry.Label,
                entry.Detail,
                entry.Active,
                (s, e) => onSlot(entry.Id)));
        }
    }

    /// <summary>
    /// The tray tooltip: what the app is currently doing.
    /// <para>
    /// Three lines rather than one. The name sits on its own line at the top, a
    /// blank line separates it, and the state is on the third. As a single line it
    /// read "Gamer Tool - Standard + Standard", which runs the app's name into the
    /// thing you actually wanted to know, and the useful half is off the end of it
    /// on a narrow taskbar.
    /// </para>
    /// <para>
    /// The lines are not centred and cannot be. A tray tooltip is drawn by the
    /// shell, not by this app, and it takes plain text: no alignment, no colour,
    /// no font. The name is on its own line and the state is on its own line, and
    /// that is as far as it goes.
    /// </para>
    /// </summary>
    /// <summary>
    /// Builds the tip text: the app's name on its own line, a blank line, then the
    /// state, clamped to what the shell will accept.
    /// <para>
    /// Out here rather than inline in <see cref="SetStatus"/> because that method
    /// needs a live shell icon to reach, so anything asserted about it in a test
    /// would be a copy of the logic rather than the logic. A test that checks a
    /// duplicate passes when the real thing breaks, which is the one thing a test
    /// is for.
    /// </para>
    /// </summary>
    public static string ComposeTip(string? state)
    {
        string clean = (state ?? string.Empty).Trim();

        string tip = clean.Length == 0
            ? Title
            : Title + "\r\n\r\n" + clean;

        // The shell allows 128 including the terminator and throws past it. Two
        // long custom preset names plus a long slot name will reach that, so the
        // clamp is the difference between a short tip and a failed call.
        return tip.Length > MaxTipLength
            ? tip.Substring(0, MaxTipLength - 1) + "…"
            : tip;
    }

    public void SetStatus(string text)
    {
        try
        {
            _icon.Text = ComposeTip(text);
        }
        catch (Exception ex)
        {
            TraceLog.Write("TRAY", ex);
        }
    }

    /// <summary>
    /// Makes the icon visible.
    /// <para>
    /// The counterpart to the window's own Hide, called when the app comes back
    /// from the tray so the icon is on screen again.
    /// </para>
    /// </summary>
    public void Show()
    {
        try
        {
            _icon.Visible = true;
        }
        catch (Exception ex)
        {
            TraceLog.Write("TRAY", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.MouseUp -= OnTrayMouseUp;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
