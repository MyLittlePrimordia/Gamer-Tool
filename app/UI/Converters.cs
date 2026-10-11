using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GamerTool.UI;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        return flag ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility visibility && visibility == Visibility.Collapsed;
    }
}

// BoolToVisibilityConverter used to sit here too. Nothing referenced it: it was
// never registered in any ResourceDictionary, so it could not be resolved by
// name from markup, and no code constructed one either. Its inverse below is the
// one that is registered, in Theme.xaml and MainWindow.xaml.
//
// Kept as a note rather than silently gone, because a converter is the kind of
// thing somebody adds "just in case" and then finds again later. If it is ever
// needed it is four lines.
