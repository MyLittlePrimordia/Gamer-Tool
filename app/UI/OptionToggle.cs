using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GamerTool.UI;

/// <summary>
/// An on/off row: a colour emoji, a label, and a switch. The emoji and the text
/// are separate properties because the row needs both, and a CheckBox only has
/// one piece of content to give.
/// </summary>
public sealed class OptionToggle : CheckBox
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(OptionToggle),
        new PropertyMetadata(string.Empty));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }


    /// <summary>Asset name under Assets\emoji, without the extension.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(OptionToggle),
        new PropertyMetadata(string.Empty));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }


    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize),
        typeof(double),
        typeof(OptionToggle),
        new PropertyMetadata(19.0));

    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }
}
