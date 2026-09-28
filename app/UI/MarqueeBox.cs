using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Size = System.Windows.Size;

namespace GamerTool.UI;

/// <summary>
/// A fixed size window onto one piece of content, which slides sideways when the
/// content turns out to be wider than the window and the pointer is resting on
/// it. Preset and app names are user editable, so a long one has to stay
/// readable without every dropdown in the row growing to fit. Nothing scrolls
/// until it is pointed at, which keeps a full page of dropdowns still.
/// <para>
/// The content is measured against infinity rather than against the box, so the
/// real width of the name is known even though the box is narrower. A Canvas
/// does that measuring for us: it never stretches or trims a child.
/// </para>
/// </summary>
public sealed class MarqueeBox : ContentControl
{
    /// <summary>
    /// Opt in, set on the control being templated rather than on this box, so a
    /// shared ComboBox template can carry the behaviour and still leave every
    /// other dropdown in the app exactly as it was.
    /// </summary>
    public static readonly DependencyProperty AllowMarqueeProperty =
        DependencyProperty.RegisterAttached(
            "AllowMarquee", typeof(bool), typeof(MarqueeBox),
            new PropertyMetadata(false, OnAllowMarqueeChanged));

    public static void SetAllowMarquee(DependencyObject element, bool value) =>
        element.SetValue(AllowMarqueeProperty, value);

    public static bool GetAllowMarquee(DependencyObject element) =>
        (bool)element.GetValue(AllowMarqueeProperty);

    /// <summary>Instance side of the flag, so the template trigger can name it.</summary>
    public bool AllowMarquee
    {
        get => GetAllowMarquee(this);
        set => SetAllowMarquee(this, value);
    }


    /// <summary>Set while the pointer is over a box that asked for the effect.</summary>
    private static readonly DependencyProperty IsMarqueeActiveProperty =
        DependencyProperty.Register(
            "IsMarqueeActive", typeof(bool), typeof(MarqueeBox),
            new PropertyMetadata(false, OnIsMarqueeActiveChanged));

    public bool IsMarqueeActive
    {
        get => (bool)GetValue(IsMarqueeActiveProperty);
        set => SetValue(IsMarqueeActiveProperty, value);
    }


    private static readonly DependencyProperty SpeedProperty =
        DependencyProperty.Register(
            "Speed", typeof(double), typeof(MarqueeBox), new PropertyMetadata(34.0));

    /// <summary>Scroll rate in pixels per second.</summary>
    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }


    private Grid? _clip;
    private ContentPresenter? _host;
    private TranslateTransform? _shift;
    private FrameworkElement? _watched;

    // The last geometry the animation was built for. Layout pass fires far more
    // often than anything actually changes size, and rebuilding the storyboard
    // every pass would restart the slide before it had visibly moved.
    private double _lastAvailable = -1;
    private double _lastNeeded = -1;
    private bool _lastActive;


    public MarqueeBox()
    {
        ClipToBounds = true;
        Focusable = false;

        // Deliberately not LayoutUpdated. That event fires on every layout pass
        // of the entire visual tree, and there is one of these per preset and per
        // app dropdown in every slot row, so a dozen of them were being asked
        // whether anything had changed dozens of times a second for the life of
        // the process. A size change on this box or on the thing it watches is the
        // only thing that can change the answer, and both say so directly.
        SizeChanged += (_, _) => Update();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _clip = GetTemplateChild("Clip") as Grid;
        _host = GetTemplateChild("Host") as ContentPresenter;

        _shift = new TranslateTransform();
        if (_host is not null)
            _host.RenderTransform = _shift;

        // A ComboBox covers its own content with a full size ToggleButton, so
        // the pointer never reaches this box. The control it is templated into
        // does see the hover, so that is what gets watched instead.
        if (_watched is FrameworkElement previous)
        {
            previous.MouseEnter -= OnHostHoverChanged;
            previous.MouseLeave -= OnHostHoverChanged;
            previous.SizeChanged -= OnWatchedSizeChanged;
        }
        _watched = ReferenceEquals(TemplatedParent, this) ? null : TemplatedParent as FrameworkElement;
        if (_watched is FrameworkElement next)
        {
            next.MouseEnter += OnHostHoverChanged;
            next.MouseLeave += OnHostHoverChanged;
            next.SizeChanged += OnWatchedSizeChanged;
        }

        _lastAvailable = -1;
        _lastNeeded = -1;
        _lastActive = false;
        SyncActive();
        Update();
    }

    /// <summary>The watched dropdown resizing can change whether the name fits.</summary>
    private void OnWatchedSizeChanged(object sender, SizeChangedEventArgs e) => Update();


    /// <summary>
    /// Measured wide open on purpose, then trimmed back to the space available.
    /// <para>
    /// Every one of these templates trims its text to the width it is given, so
    /// an ordinary measure reports the clipped width and the name always looks
    /// like it fits, which means the slide would never start. Measuring against
    /// infinity gives the real width of the name, and the return value is capped
    /// so the row around it is laid out exactly as it was before.
    /// </para>
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_clip is null)
            return base.MeasureOverride(availableSize);

        _clip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size full = _clip.DesiredSize;

        return new Size(
            double.IsInfinity(availableSize.Width) ? full.Width : Math.Min(full.Width, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? full.Height : Math.Min(full.Height, availableSize.Height));
    }


    private static void OnAllowMarqueeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarqueeBox box)
            box.SyncActive();
    }


    private void OnHostHoverChanged(object sender, MouseEventArgs e) => SyncActive();

    /// <summary>
    /// The name changing is the other half of what has to be noticed.
    /// <para>
    /// The box's own size does not move when a longer preset name is selected,
    /// so a size change on its own says nothing, and the width that decides
    /// whether the slide is needed comes from the content's measured size, which
    /// is only final once layout has run. So a content change schedules an update
    /// after layout rather than doing it inline. It matters most after a restore,
    /// which swaps every name in the dropdowns at once with nothing under the
    /// pointer to cause a hover and give the answer away.
    /// </para>
    /// </summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == ContentProperty)
        {
            // Invalidated rather than updated: the new width is not known yet.
            _lastAvailable = -1;
            _lastNeeded = -1;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Update));
        }
    }

    private void SyncActive()
    {
        bool over = _watched is not null ? _watched.IsMouseOver : IsMouseOver;
        if (IsMarqueeActive != (AllowMarquee && over))
            IsMarqueeActive = AllowMarquee && over;
    }


    private static void OnIsMarqueeActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarqueeBox)d).Update();


    private void Update()
    {
        if (_clip is null || _host is null || _shift is null)
            return;

        double available = _clip.ActualWidth;

        // The content's own DesiredSize is the untrimmed width of the name, which
        // is the only way to know whether it does not fit.
        double needed = _host.DesiredSize.Width;
        bool active = IsMarqueeActive;

        if (Math.Abs(available - _lastAvailable) < 0.5
            && Math.Abs(needed - _lastNeeded) < 0.5
            && active == _lastActive)
        {
            return;
        }

        _lastAvailable = available;
        _lastNeeded = needed;
        _lastActive = active;
        Restart(available, needed);
    }


    private void Restart(double available, double needed)
    {
        if (_shift is null)
            return;

        // Dropping the animation returns the property to its base value, which
        // is where the slide starts, so leaving the box also parks the name.
        _shift.BeginAnimation(TranslateTransform.XProperty, null);

        if (!IsMarqueeActive)
            return;

        double overflow = needed - available;
        if (available <= 1 || overflow <= 1)
            return;

        // Constant speed regardless of how much is hidden, with a beat before
        // the first move so a passing pointer does not set every name going.
        double seconds = Math.Max(0.6, overflow / Math.Max(1.0, Speed));

        _shift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(0, -overflow, TimeSpan.FromSeconds(seconds))
            {
                BeginTime = TimeSpan.FromSeconds(0.45),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

}
