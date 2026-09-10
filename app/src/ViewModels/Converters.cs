using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GamerTool.ViewModels;

/// <summary>
/// Inverse BooleanToVisibilityConverter: true -> Collapsed, false/null -> Visible.
/// Used for the "install engine" card that must disappear once an engine exists.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>
/// StringToVisibilityConverter: non-empty (after trim) -> Visible, else Collapsed.
/// Used for the install progress text so it takes no space when idle.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
