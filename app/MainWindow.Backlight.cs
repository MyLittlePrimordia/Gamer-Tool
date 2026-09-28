using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Slider = System.Windows.Controls.Slider;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace GamerTool;



/// The hardware backlight section on the Display tab: one row per display.
/// <para>
/// Every row carries its own state and never consults a machine wide answer,
/// because a machine with a DDC/CI capable monitor next to one that has no
/// pathway at all is ordinary, and a shared flag would grey out the good one.
/// Each row also owns the debounce for its own slider, so dragging one display
/// never delays another.
/// </para>
/// </summary>
public partial class MainWindow
{
    private BacklightService? _backlight;

    private readonly Dictionary<string, DispatcherTimer> _backlightDebounce = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many re-sends a display gets when the bus was busy rather than
    /// refusing. See BacklightWriteRetry for why the budget is one.
    /// </summary>
    private readonly BacklightWriteRetry _backlightRetry = new();


    /// <summary>
    /// The brightness each monitor's debounce is currently waiting to write, keyed
    /// by device name. The value lives here rather than in the tick handler's
    /// closure because the handler is built once per monitor and then reused for
    /// every later drag: captured, it wrote the value from the first pixel of the
    /// drag and left the monitor disagreeing with its own slider.
    /// </summary>
    private readonly Dictionary<string, uint> _pendingBacklightValues = new(StringComparer.OrdinalIgnoreCase);


    private BacklightService Backlight =>
        _backlight ??= new BacklightService(_settings);


    /// <summary>
    /// Draws the section. Called when the tab is shown, when the probe finishes
    /// and when the option is switched, so the row always describes the truth as
    /// of the last check rather than what was hoped for at startup.
    /// </summary>
    private void RefreshBacklightRows()
    {
        if (BacklightPanel is null)
        {
            return;
        }

        BacklightPanel.Children.Clear();

        if (!Backlight.IsEnabled)
        {
            // A heading and a button, not a sentence. It used to be thirteen words
            // of grey instruction sitting between two rules in the middle of the
            // page, naming a control that lives on another tab and is already
            // labelled in plain English there. Turning the feature on is one click
            // from here now instead of find the tab, find the row, find the switch.
            StackPanel off = new()
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            off.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("SectionHeader"),
                Text = "HARDWARE BRIGHTNESS OFF",
                VerticalAlignment = VerticalAlignment.Center
            });

            Button turnOn = new()
            {
                Content = "Turn on",
                Style = (Style)FindResource("GhostButton"),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 92,
                ToolTip = "Control the monitor's own backlight over DDC/CI"
            };
            turnOn.Click += OnTurnOnHardwareBrightness;
            off.Children.Add(turnOn);

            BacklightPanel.Children.Add(off);
            return;
        }

        if (!Backlight.HasProbed)
        {
            BacklightPanel.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("CardSub"),
                Text = "Checking what this display can do..."
            });
            return;
        }

        IReadOnlyList<MonitorProbe> monitors = Backlight.Monitors;
        if (monitors.Count == 0)
        {
            BacklightPanel.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("CardSub"),
                Text = "No displays found."
            });
            return;
        }

        TextBlock heading = new()
        {
            Style = (Style)FindResource("SectionHeader"),
            Text = monitors.Count == 1 ? "HARDWARE BRIGHTNESS" : "HARDWARE BRIGHTNESS PER DISPLAY",
            Margin = new Thickness(0, 0, 0, 8)
        };
        BacklightPanel.Children.Add(heading);

        foreach (MonitorProbe monitor in monitors)
        {
            BacklightPanel.Children.Add(BacklightRow(monitor));
        }
    }


    private UIElement BacklightRow(MonitorProbe monitor)
    {
        bool live = monitor.CanControlBacklight;

        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // A third column, for the hint button. It was being added at index 2 into
        // a grid that only had two, and WPF quietly puts an out of range child in
        // the last cell that does exist, so the button landed on top of the badge
        // and covered the verdict it was there to explain.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });


        StackPanel left = new();
        left.Children.Add(new TextBlock
        {
            Text = monitor.FriendlyName,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = live
                ? (Brush)FindResource("TextHi")
                : (Brush)FindResource("TextLow"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // A display that has refused a write but is still being tried says so on
        // its own row, rather than only in a toast that has already scrolled away.
        // Without this a refusal looks exactly like a display that is working.
        int refusals = Backlight.Refusals.TryGetValue(monitor.DeviceName, out int seen) ? seen : 0;
        if (live && refusals > 0)
        {
            left.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("CardSub"),
                Foreground = (Brush)FindResource("Amber"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = "Display did not take that value. Trying again on the next move."
            });
        }

        // No tooltip here on purpose. It used to carry one and it carried the
        // sentence the row already shows, so hovering a line of text produced a
        // duplicate of itself. The only thing on this row worth explaining is
        // the small button, and that has its own.
        left.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = live
                ? "Monitor's own backlight, over DDC/CI. Your brightness slider above bends the picture instead."
                : Backlight.CapabilityOf(monitor)
        });
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        // A working display keeps the quiet cyan pill. An unsupported one drops it
        // and shouts in amber instead: a grey pill either side of a small grey
        // word was easy to walk past, and this is the one row on the page that
        // is telling the user something they can act on.
        Border badge = new()
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = live
                ? new SolidColorBrush(Color.FromArgb(0x1A, 0x22, 0xD3, 0xEE))
                : System.Windows.Media.Brushes.Transparent
        };

        badge.Child = new TextBlock
        {
            Text = live ? "DDC/CI" : "Not Supported",
            FontSize = live ? 8.5 : 10.5,
            FontWeight = live ? FontWeights.SemiBold : FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = live
                ? (Brush)FindResource("AccentDisplay")
                : (Brush)FindResource("Amber")
        };
        Grid.SetColumn(badge, 1);
        row.Children.Add(badge);

        // One small way out of a dead end, and only for the one dead end a
        // setting on the GPU can undo. It sits beside the badge rather than
        // inside it, because inside the pill it read as part of the label rather
        // than as something to press. A button rather than a picture, so it
        // answers the keyboard and can be named to a screen reader, which is the
        // whole difference between a hint and a decoration.
        if (DdcDriverHint.ShouldOffer(monitor))
        {
            // Styled like a nav tab: nothing until hovered, then a faint wash, so
            // the button is discoverable without competing with the verdict. All
            // of the chrome lives in the style, because setting a Background here
            // is what let it paint over the badge before the columns were fixed.
            Button why = new()
            {
                Style = (Style)FindResource("InfoHintButton"),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Why is this not working?",
                Content = new UI.EmojiImage { Glyph = "info", Width = 15, Height = 15 }
            };

            AutomationProperties.SetName(why, "Why hardware brightness is unavailable on this display");

            why.Click += (s, e) =>
            {
                HintContent hint = DdcDriverHint.For(DdcDriverHint.Vendor);

                // The steps are numbered rather than bulleted so the order reads
                // as an order, which is what they are: the Overrides page is not
                // visible until the EULA has been accepted.
                string body = string.Join(
                    Environment.NewLine,
                    hint.Steps.Select((step, index) => (index + 1) + ".  " + step));

                ShowInfoModal(hint.Title, body, hint.Warning);
            };

            Grid.SetColumn(why, 2);
            row.Children.Add(why);
        }

        if (!live)

        {
            // Nothing to drag. A live looking slider over a display that cannot
            // be driven is the exact sort of lie this app has been fixing all
            // session, so there is no slider at all here.
            //
            // The dimming applies to the text only, not to the whole row. It used
            // to wrap the row in a Border at 0.75, which dragged the badge and
            // the hint button down with it and left the info glyph a flat grey
            // outline. Opacity is composited, so a child cannot opt out of it
            // from inside: it has to not be applied in the first place. The
            // emoji are baked in colour precisely so they can be read, and the
            // only thing here meant to be muted is the sentence beside them.
            left.Opacity = 0.75;

            return new Border
            {
                Child = row,
                Margin = new Thickness(0, 0, 0, 10)
            };
        }


        StackPanel block = new();
        block.Children.Add(row);

        BrightnessReading reading = monitor.Brightness!;
        Slider slider = new()
        {
            Style = (Style)FindResource("ModernSlider"),
            Foreground = (Brush)FindResource("AccentDisplay"),
            Minimum = reading.Minimum,
            Maximum = reading.Maximum,
            Value = reading.Current,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = "Monitor brightness"
        };

        TextBlock value = new()
        {
            Style = (Style)FindResource("Value"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = reading.Current + " / " + reading.Maximum
        };

        slider.ValueChanged += (s, e) =>
        {
            uint wanted = (uint)Math.Round(slider.Value);
            value.Text = wanted + " / " + reading.Maximum;
            QueueBacklightWrite(monitor, wanted);
        };

        block.Children.Add(slider);
        block.Children.Add(value);
        block.Margin = new Thickness(0, 0, 0, 12);
        return block;
    }


    /// <summary>
    /// Flips the hardware brightness option from here rather than making the
    /// reader go and find it on another tab. The option's own handler starts the
    /// probe, so this only has to set the switch.
    /// </summary>
    private void OnTurnOnHardwareBrightness(object sender, RoutedEventArgs e)
    {
        if (_settings.HardwareBrightnessEnabled)
        {
            return;
        }

        HardwareBrightnessBox.IsChecked = true;
    }

    /// <summary>
    /// Holds a drag still for a moment before it touches the bus. The engine
    /// wants at most one write every 120ms or so; sending a write per pixel of
    /// mouse movement is how a slider ends up looking broken on a slow scaler.
    /// <para>
    /// Also the way a re-sent value goes back out, which is why it takes only the
    /// value and not the slider: a retry has no slider to hand.
    /// </para>
    /// </summary>
    private void QueueBacklightWrite(MonitorProbe monitor, uint value)
    {
        if (!_backlightDebounce.TryGetValue(monitor.DeviceName, out DispatcherTimer? timer))
        {
            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(HardwareBrightness.MinimumWriteGapMs)
            };

            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (_pendingBacklightValues.TryGetValue(monitor.DeviceName, out uint wanted))
                {
                    _pendingBacklightValues.Remove(monitor.DeviceName);
                    SendBacklightWrite(monitor, wanted);
                }
            };

            _backlightDebounce[monitor.DeviceName] = timer;
        }

        // Recorded on every call, so the tick always writes where the drag
        // actually ended rather than where it started.
        _pendingBacklightValues[monitor.DeviceName] = value;
        timer.Stop();
        timer.Start();
    }


    private void SendBacklightWrite(MonitorProbe monitor, uint value)
    {
        _ = Task.Run(() =>
        {
            BusOutcome outcome = Backlight.TrySet(monitor, value, out string? why);
            bool settingsChanged = Backlight.ConsumeSettingsChanged();

            Dispatcher.InvokeAsync(() =>
            {
                if (settingsChanged)
                {
                    // The exclusion list only means something if it survives a
                    // restart. It used to be added in memory and then forgotten,
                    // so a display the app had given up on came back on the next
                    // launch with no explanation and no way to clear it.
                    Commit();
                }

                // The bus was busy, so the display was never asked and the value
                // is still wanted. Put it back and let the debounce carry it
                // once the other monitor's transaction has finished. Without this
                // the row sits at a brightness the panel is not at, and the only
                // symptom is a slider that looks stuck.
                //
                // This has to happen here rather than on the worker: the re-send
                // restarts a DispatcherTimer, which belongs to the UI thread.
                if (outcome == BusOutcome.Busy)
                {
                    if (_backlightRetry.ShouldRetry(monitor.DeviceName))
                    {
                        AppLog.Info("backlight write to " + monitor.FriendlyOrDevice()
                            + " was skipped because the bus was busy, re-sending "
                            + value.ToString(CultureInfo.InvariantCulture)
                            + " once ("
                            + _backlightRetry.Remaining(monitor.DeviceName).ToString(CultureInfo.InvariantCulture)
                            + " re-send left)");
                        QueueBacklightWrite(monitor, value);
                        return;
                    }

                    AppLog.Warn("backlight write to " + monitor.FriendlyOrDevice()
                        + " still could not get the bus after "
                        + BacklightWriteRetry.MaxRetries.ToString(CultureInfo.InvariantCulture)
                        + " re-send; leaving the value alone. The row may not match the panel.");
                }
                else
                {
                    _backlightRetry.Reset(monitor.DeviceName);
                }

                if (outcome != BusOutcome.Ok)
                {
                    Flash(why ?? "The monitor refused the brightness", true);
                }

                RefreshBacklightRows();
            });
        });
    }



    /// <summary>
    /// Puts a sanitised diagnostic summary on the clipboard. The log is read,
    /// scrubbed of anything that names the person using the machine, and joined
    /// by a short header describing the machine and what the app decided about it.
    /// </summary>
    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = AppLog.Summary(() =>
            {
                StringBuilder extra = new();
                extra.AppendLine("displays:");
                extra.AppendLine(AppLog.BacklightLines(Backlight.Monitors));
                extra.AppendLine("hardware brightness: " + (Backlight.IsEnabled ? "on" : "off"));
                extra.AppendLine("excluded from probing: "
                    + (_settings.ExcludedDdcMonitors.Count == 0
                        ? "none"
                        : string.Join(", ", _settings.ExcludedDdcMonitors)));
                return extra.ToString();
            });

            Clipboard.SetText(text);
            ShowDiagFeedback("Copied to clipboard", ok: true);
            Flash("Diagnostics copied");
        }
        catch (Exception ex)
        {
            // The clipboard is shared with everything else on the machine and can
            // be locked by another app, so failing here is ordinary, not fatal.
            AppLog.Error("COPY DIAGNOSTICS", ex);
            ShowDiagFeedback("Could not copy", ok: false);
            Flash("Could not copy the log", true);
        }
    }

    /// <summary>
    /// Stops the folder button's click from also reaching the row.
    /// <para>
    /// The diagnostics row has copied on a click anywhere on it for a long time,
    /// which is the right behaviour for one action on a row. With a second action
    /// sharing the row it stops being right: without this, clicking the folder
    /// would open Explorer and copy the log at the same time. Preview, not
    /// bubble, because the row listens for the bubbled event.
    /// </para>
    /// </summary>
    private void OnOpenLogFolderPreview(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>
    /// Opens the folder the log is written into.
    /// <para>
    /// Copying to the clipboard answers "what did it decide", which is not what
    /// most people asking for a log actually want. They want to read the log
    /// themselves, and the file is rotated across several, so a folder beats a
    /// single pasted block. Both are offered rather than one being chosen.
    /// </para>
    /// </summary>
    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Created on the way through, because a machine that has never hit
            // anything worth logging has no folder yet, and a button that fails
            // for want of a folder is worse than no button.
            Directory.CreateDirectory(AppLog.Folder);
            Process.Start(new ProcessStartInfo { FileName = AppLog.Folder, UseShellExecute = true });
            Flash("Log folder opened");
        }
        catch (Exception ex)
        {
            AppLog.Error("OPEN LOG FOLDER", ex);
            Flash("Could not open the log folder", true);
        }
    }



    /// <summary>
    /// Swaps the clipboard mark for a short confirmation, then puts the mark back.
    /// <para>
    /// The row is one line of list, so the mark's own space is the only place a
    /// confirmation fits without putting the row back to a heading and a line of
    /// explanation and needing a divider of its own again. Anything longer than a
    /// few words still goes to the status line through <see cref="Flash"/>, which
    /// is where the full text of a failure belongs.
    /// </para>
    /// </summary>
    private void ShowDiagFeedback(string message, bool ok)
    {
        DiagCopiedText.Text = message;
        DiagCopiedText.Foreground = (System.Windows.Media.Brush)FindResource(
            ok ? "AccentSettings" : "Red");
        DiagCopiedText.Visibility = Visibility.Visible;
        CopyDiagButton.Visibility = Visibility.Collapsed;

        if (_diagFeedbackTimer is null)
        {
            _diagFeedbackTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2.4)
            };
            _diagFeedbackTimer.Tick += OnDiagFeedbackTick;
        }

        _diagFeedbackTimer.Stop();
        _diagFeedbackTimer.Start();
    }


    private void OnDiagFeedbackTick(object? sender, EventArgs e)
    {
        _diagFeedbackTimer?.Stop();
        DiagCopiedText.Visibility = Visibility.Collapsed;
        CopyDiagButton.Visibility = Visibility.Visible;
    }
}

