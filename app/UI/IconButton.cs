using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GamerTool.UI;

/// <summary>
/// A square, icon only button whose glyph is a vector outline rather than a
/// character, so it renders identically everywhere and can be tinted.
/// </summary>
public sealed class IconButton : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(Geometry),
        typeof(IconButton),
        new PropertyMetadata(null));

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }


    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize),
        typeof(double),
        typeof(IconButton),
        new PropertyMetadata(15.0));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }
}
