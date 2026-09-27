using System;
using System.Windows;
using System.Windows.Controls;

namespace GamerTool.UI;

/// <summary>
/// Gives a fader a magnet at its neutral value.
/// </summary>
/// <remarks>
/// WPF sliders have no detents, so a fader nudged a little off centre sits there
/// looking like a deliberate setting when it is really drift, and there is no
/// quick way back. When a value comes within <c>Threshold</c> of <c>Neutral</c>
/// it is pulled the rest of the way, which is the same idea as a detent but only
/// at the one point that matters.
/// </remarks>
public static class SliderSnap
{
    /// <summary>The value the fader is pulled to. Unset by default, so a slider only snaps if it is asked to.</summary>
    public static readonly DependencyProperty NeutralProperty = DependencyProperty.RegisterAttached(
        "Neutral",
        typeof(double),
        typeof(SliderSnap),
        new PropertyMetadata(double.NaN, OnSnapChanged));

    /// <summary>How close the fader has to be before the magnet takes hold.</summary>
    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.RegisterAttached(
        "Threshold",
        typeof(double),
        typeof(SliderSnap),
        new PropertyMetadata(0.0, OnSnapChanged));

    /// <summary>Set once a slider has been hooked, so it is only wired up a single time.</summary>
    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked",
        typeof(bool),
        typeof(SliderSnap),
        new PropertyMetadata(false));

    public static void SetNeutral(DependencyObject element, double value) => element.SetValue(NeutralProperty, value);

    public static double GetNeutral(DependencyObject element) => (double)element.GetValue(NeutralProperty);

    public static void SetThreshold(DependencyObject element, double value) => element.SetValue(ThresholdProperty, value);

    public static double GetThreshold(DependencyObject element) => (double)element.GetValue(ThresholdProperty);

    private static void OnSnapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider || (bool)slider.GetValue(HookedProperty))
        {
            return;
        }

        slider.SetValue(HookedProperty, true);
        slider.ValueChanged += OnValueChanged;
    }

    private static void OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider)
        {
            return;
        }

        double neutral = GetNeutral(slider);
        if (double.IsNaN(neutral))
        {
            return;
        }

        double gap = e.NewValue - neutral;

        // Outside the magnet, or already sitting exactly on it. The second test
        // also stops the assignment below re-entering for ever.
        if (Math.Abs(gap) > GetThreshold(slider) || Math.Abs(gap) < 0.0001)
        {
            return;
        }

        slider.Value = neutral;
    }
}
