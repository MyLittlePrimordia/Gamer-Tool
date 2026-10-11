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


    /// <summary>
    /// An asset name shown between the label and the switch, on the same row.
    /// <para>
    /// Added for one caller: a setting that turns itself off, and has to be able
    /// to say why without printing a line of text under a list of switches. The
    /// alternative was an amber sentence in its own row, which is three lines of
    /// prose in the middle of a column of switches and pushed the last row of the
    /// settings list under the fold.
    /// </para>
    /// <para>
    /// Empty by default, and the template collapses the whole element when it is,
    /// so every other row using this style renders exactly as it did before. The
    /// width is an Auto column, so a collapsed badge also collapses its column.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty BadgeGlyphProperty = DependencyProperty.Register(
        nameof(BadgeGlyph),
        typeof(string),
        typeof(OptionToggle),
        new PropertyMetadata(string.Empty));

    public string BadgeGlyph
    {
        get => (string)GetValue(BadgeGlyphProperty);
        set => SetValue(BadgeGlyphProperty, value ?? string.Empty);
    }


    /// <summary>
    /// Hover text for the badge, and its accessible name.
    /// <para>
    /// The glyph alone says nothing to a screen reader, and an unlabelled button
    /// in a list of switches is one of the least guessable things in the app.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty BadgeToolTipProperty = DependencyProperty.Register(
        nameof(BadgeToolTip),
        typeof(string),
        typeof(OptionToggle),
        new PropertyMetadata(string.Empty));

    public string BadgeToolTip
    {
        get => (string)GetValue(BadgeToolTipProperty);
        set => SetValue(BadgeToolTipProperty, value);
    }


    /// <summary>
    /// Raised when the badge is clicked. The badge is the only part of this row
    /// that is not the switch, so it announces itself rather than toggling.
    /// </summary>
    public event RoutedEventHandler? BadgeClick;


    private const string BadgePartName = "PART_Badge";

    private Button? _badge;

    /// <summary>
    /// Wires the badge up in code, because a ControlTemplate cannot reach a
    /// handler in the control that owns it.
    /// <para>
    /// The badge is a real Button rather than a glyph so that it answers the
    /// keyboard and can be named. WPF marks a Button's mouse events handled on
    /// the way up, so clicking it does not also toggle the switch it sits inside.
    /// </para>
    /// </summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_badge is not null)
        {
            _badge.Click -= OnBadgeClick;
        }

        _badge = GetTemplateChild(BadgePartName) as Button;
        if (_badge is not null)
        {
            _badge.Click += OnBadgeClick;
        }
    }

    private void OnBadgeClick(object sender, RoutedEventArgs e)
    {
        BadgeClick?.Invoke(this, e);
    }
}
