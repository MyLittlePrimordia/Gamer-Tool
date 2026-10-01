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
            // Nothing at all.
            //
            // There used to be a "Hardware brightness off" heading here with a Turn
            // on button beside it, on the reasoning that naming the feature was
            // helpful. It is the same reasoning that produced the diagnostic advice
            // this app no longer gives: the switch is on the Settings tab, is
            // labelled in plain English, and a user who wants this feature turns it
            // on there. A heading and a button for a feature that is switched off
            // is an advertisement for it on the one screen the user is not using,
            // and a sign that the store is opening soon is worse than no sign at
            // all.
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
            // The Label style, not SectionHeader, so this matches Gamma, Shadow
            // boost, Colour trim and Blue light filter. It was a SectionHeader, which
            // is 9.5 point semi-bold in the dimmest text colour, so the one heading
            // among the slider labels was smaller, dimmer and a different weight
            // from the six labels sitting beside it. Every control on this panel is
            // a display slider; nothing here is a section of its own.
            Style = (Style)FindResource("Label"),
            Text = monitors.Count == 1 ? "Hardware brightness" : "Hardware brightness per display",
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


        // Two columns: the name and its status, then the verdict badge. This used to
        // have a third for a small info button that opened a set of steps for
        // getting DDC/CI working. It is gone, and the reason is worth recording so
        // it does not come back: on real hardware the steps were confidently wrong,
        // sending people to change a graphics setting that was already correct
        // through several restarts, and there was no way to prove they were right
        // in the first place. A hint that is wrong is worse than no hint, because
        // the only way to tell it apart from a bug is to have followed it.
        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
            Text = BacklightStatus.For(monitor)
        });
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        // A working display keeps the quiet cyan pill. An unsupported one shouts in
        // amber instead: a grey pill either side of a small grey word was easy to
        // walk past.
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

        if (!live)
        {
            // No slider here, and not a greyed out one. A disabled control is still
            // a control: it is announced by a screen reader as something to
            // operate, and there is no brightness reading behind it, so any
            // position drawn for it would be invented. The monitor's name and the
            // reason are the whole of what is worth showing.
            //
            // The dimming applies to the text only, not to the whole row. Wrapping
            // the row instead used to drag the badge down with it, and opacity is
            // composited, so a child cannot opt out of it from inside: it has to
            // not be applied in the first place.
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

                // Read under the profile's own gate, like every other reader of
                // that list. It is the one the backlight worker appends to from a
                // pool thread, and the whole reason the gate exists is that
                // enumerating a List while something adds to it is not safe. A
                // torn read here cannot throw on a string list, so it would not
                // have shown up as a crash - only as a blank or stale line in
                // the one artefact a user is asked to paste into a bug report.
                List<string> excluded;
                lock (_settings.Gate)
                {
                    excluded = _settings.ExcludedDdcMonitors.ToList();
                }

                extra.AppendLine("excluded from probing: "
                    + (excluded.Count == 0 ? "none" : string.Join(", ", excluded)));
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
    /// Shows the confirmation beside the mark, then takes it away again.
    /// <para>
    /// The mark is left alone. It used to be hidden for as long as the
    /// confirmation was up, on the reasoning that the row is one line and one
    /// piece of text is all that fits. But the mark is the only thing on the row
    /// that says which action was taken, so hiding it to announce that the action
    /// had worked removed the answer at the moment it was wanted. They sit side
    /// by side now, which the row has the width for, and the text has its own
    /// column to do it in.
    /// </para>
    /// <para>
    /// Anything longer than a few words still goes to the status line through
    /// <see cref="Flash"/>, which is where the full text of a failure belongs.
    /// </para>
    /// </summary>
    private void ShowDiagFeedback(string message, bool ok)
    {
        DiagCopiedText.Text = message;
        DiagCopiedText.Foreground = (System.Windows.Media.Brush)FindResource(
            ok ? "AccentSettings" : "Red");
        DiagCopiedText.Visibility = Visibility.Visible;

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
    }
}

