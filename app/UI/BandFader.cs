using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace GamerTool.UI;

/// <summary>
/// A vertical fader that can draw its groove as a row of dashes instead of a
/// solid line, and can drop the small dash that marks the zero point.
///
/// The engine draws its equaliser bands as dashed lines with no marker at zero,
/// while its colour trims are solid and do mark the middle. Rather than keep two
/// near identical control templates in step, the one template asks this flag.
///
/// The dashes are real rectangles rather than a dashed stroke or a tiled brush.
/// A stroke needs a path with some width to run along, and a tiling brush was
/// measured rendering nothing at all here, so the only dependable way to get
/// evenly spaced marks down a track of unknown height is to lay them out.
/// </summary>
public sealed class BandFader : Slider
{
    /// <summary>How long each dash is, and the gap that follows it.</summary>
    private const double DashLength = 5.0;

    private const double DashGap = 6.0;

    public static readonly DependencyProperty DashedProperty = DependencyProperty.Register(
        nameof(Dashed),
        typeof(bool),
        typeof(BandFader),
        new FrameworkPropertyMetadata(false, OnDashedChanged));

    /// <summary>True for a dashed groove with no zero marker, as the engine draws its bands.</summary>
    public bool Dashed
    {
        get => (bool)GetValue(DashedProperty);
        set => SetValue(DashedProperty, value);
    }

    private static void OnDashedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((BandFader)d).ApplyBandStyle();
        ((BandFader)d).LayoutDashes();
    }

    private Canvas? _dashHost;

    private Brush? _dashBrush;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _dashHost = GetTemplateChild("DashHost") as Canvas;
        if (_dashHost is not null)
        {
            _dashHost.SizeChanged += (_, _) => LayoutDashes();
        }

        ApplyBandStyle();
        LayoutDashes();
    }

    /// <summary>
    /// Swaps the solid groove, the zero marker and the coloured bars for the
    /// dashed line the engine draws its bands with.
    ///
    /// This is done in code rather than with a template trigger because the
    /// trigger was only taking effect on some of the elements it named: the fills
    /// collapsed but the groove behind the dashes stayed, which left a solid bar
    /// showing through the gaps. Owning all four elements in one place means they
    /// change together or not at all.
    /// </summary>
    private void ApplyBandStyle()
    {
        bool band = Dashed;
        SetVisibility("Groove", band ? Visibility.Collapsed : Visibility.Visible);
        SetVisibility("CentreTick", band ? Visibility.Collapsed : Visibility.Visible);
        SetVisibility("UpperFill", band ? Visibility.Collapsed : Visibility.Visible);
        SetVisibility("LowerFill", band ? Visibility.Collapsed : Visibility.Visible);

        if (_dashHost is not null)
        {
            _dashHost.Visibility = band ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SetVisibility(string name, Visibility visibility)
    {
        if (GetTemplateChild(name) is FrameworkElement element)
        {
            element.Visibility = visibility;
        }
    }

    /// <summary>Fills the host with as many dashes as the current track height needs.</summary>
    private void LayoutDashes()
    {
        if (_dashHost is null)
        {
            return;
        }

        _dashHost.Children.Clear();

        if (!Dashed)
        {
            return;
        }

        double height = _dashHost.ActualHeight;
        double width = _dashHost.ActualWidth;
        if (height <= 0.0 || width <= 0.0)
        {
            // The track has not been measured yet. The size change will call back.
            return;
        }

        _dashBrush ??= ResolveGrooveBrush();

        double step = DashLength + DashGap;
        for (double y = 0.0; y < height; y += step)
        {
            var dash = new Rectangle
            {
                Width = width,
                Height = Math.Min(DashLength, height - y),
                Fill = _dashBrush
            };
            Canvas.SetTop(dash, y);
            Canvas.SetLeft(dash, 0.0);
            _dashHost.Children.Add(dash);
        }
    }

    /// <summary>
    /// Takes the groove colour from the theme so the dashes cannot drift away
    /// from the solid groove the colour trims still use.
    /// </summary>
    private Brush ResolveGrooveBrush()
    {
        object? themed = Resources["Groove"];
        if (themed is Brush themedBrush)
        {
            return themedBrush;
        }

        return new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    }
}
