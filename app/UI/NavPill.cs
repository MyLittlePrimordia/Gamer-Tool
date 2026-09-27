using System.Windows;
using System.Windows.Controls;

namespace GamerTool.UI;

/// <summary>
/// The nav tabs are RadioButtons so only one can be active at a time. This
/// subclass exists to give them a distinct XAML type, so the tab styles using a
/// RadioButton implicit style do not drag every other radio button on a page into
/// the tab bar template, and to carry the name of the emoji bitmap the tab shows.
/// </summary>
public sealed class NavPill : RadioButton
{
    /// <summary>Asset name under Assets\emoji, without the extension.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(NavPill),
        new PropertyMetadata(string.Empty));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
}
