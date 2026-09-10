using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GamerTool.Core;

namespace GamerTool.ViewModels;

/// <summary>
/// Icon catalog key (persisted string like "bullseye") -> ImageSource for
/// XAML Image.Source bindings. Returns null for unknown/empty keys so the
/// Image simply renders nothing.
/// </summary>
public sealed class IconKeyToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => IconCatalog.GetIcon(value as string);

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
