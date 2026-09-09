using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GamerTool.ViewModels;

/// <summary>
/// MultiBinding converter for the preset cards: returns true when the card's
/// item (first binding) is the same object as the view model's currently
/// selected preset (second binding). Drives the "selected card" highlight
/// without needing selection state stored on the model classes.
/// </summary>
public sealed class SelectedItemMatchConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && ReferenceEquals(values[0], values[1]);

    public object?[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
