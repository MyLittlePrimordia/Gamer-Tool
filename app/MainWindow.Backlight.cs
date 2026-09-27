using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Slider = System.Windows.Controls.Slider;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace GamerTool;

/// <summary>
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
            BacklightPanel.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("CardSub"),
                Text = "Hardware brightness is off. Turn it on in Settings to control the monitor's own backlight."
            });
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

        left.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardSub"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = Backlight.CapabilityOf(monitor),
            Text = live
                ? "Monitor's own backlight, over DDC/CI. Your brightness slider above bends the picture instead."
                : Backlight.CapabilityOf(monitor)
        });
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        // The badge states the monitor's own verdict. It is never a shared one.
        Border badge = new()
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = live
                ? new SolidColorBrush(Color.FromArgb(0x1A, 0x22, 0xD3, 0xEE))
                : new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            ToolTip = Backlight.CapabilityOf(monitor)
        };
        badge.Child = new TextBlock
        {
            Text = live ? "DDC/CI" : "NOT SUPPORTED",
            FontSize = 8.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = live
                ? (Brush)FindResource("AccentDisplay")
                : (Brush)FindResource("TextLow")
        };
        Grid.SetColumn(badge, 1);
        row.Children.Add(badge);

        if (!live)
        {
            // Nothing to drag. A live looking slider over a display that cannot
            // be driven is the exact sort of lie this app has been fixing all
            // session, so there is no slider at all here.
            return new Border
            {
                Child = row,
                Margin = new Thickness(0, 0, 0, 10),
                Opacity = 0.75
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
            QueueBacklightWrite(monitor, slider, wanted);
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
    /// </summary>
    private void QueueBacklightWrite(MonitorProbe monitor, Slider slider, uint value)
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
            bool ok = Backlight.TrySet(monitor, value, out string? why);

            Dispatcher.InvokeAsync(() =>
            {
                if (!ok)
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
            DiagHintText.Text = "Copied. Paste it anywhere.";
            Flash("Diagnostics copied");
        }
        catch (Exception ex)
        {
            // The clipboard is shared with everything else on the machine and can
            // be locked by another app, so failing here is ordinary, not fatal.
            AppLog.Error("COPY DIAGNOSTICS", ex);
            DiagHintText.Text = "Could not reach the clipboard, try again.";
            Flash("Could not copy the log", true);
        }
    }
}
