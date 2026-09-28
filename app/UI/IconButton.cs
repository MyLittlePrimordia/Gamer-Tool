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
        new PropertyMetadata(null, OnIconChanged));

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>
    /// The correction that puts the glyph's visual mass in the middle of its own
    /// bounding box, applied to the drawn path rather than baked into the geometry.
    /// <para>
    /// It has to be a transform on the path, and not a nudge inside the geometry,
    /// because the glyph is drawn through a Viewbox and a Viewbox centres the
    /// bounding box. Moving a shape moves the box it is measured in, and the box
    /// is then re-centred, which cancels the move exactly. So the bounding box
    /// stays put and the ink is moved instead.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty IconNudgeProperty = DependencyProperty.Register(
        nameof(IconNudge),
        typeof(Transform),
        typeof(IconButton),
        new PropertyMetadata(Transform.Identity));

    public Transform IconNudge
    {
        get => (Transform)GetValue(IconNudgeProperty);
        private set => SetValue(IconNudgeProperty, value);
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        System.Windows.Point offset = Icons.OpticalOffset(e.NewValue as Geometry);
        ((IconButton)d).IconNudge = offset == default
            ? Transform.Identity
            : new TranslateTransform(offset.X, offset.Y);
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
