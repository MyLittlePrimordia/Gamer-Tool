using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace GamerTool.UI;

public partial class RetroOsd : Window
{
    private const int GwlExStyle = -20;

    private const int WsExTransparent = 0x00000020;

    private const int WsExNoActivate = 0x08000000;

    private const int WsExTopmost = 0x00000008;

    private const int WsExToolWindow = 0x00000080;
public RetroOsd()
    {
        InitializeComponent();

        HideTimer = new DispatcherTimer
        {
            // A shade longer than the timeline, so the window is not taken off
            // screen while the last frame of the fade is still being painted.
            Interval = TimeSpan.FromMilliseconds(VisibleMilliseconds + 120.0)
        };

        HideTimer.Tick += OnHideTimerTick;

        Loaded += OnOsdLoaded;
    }

    /// <summary>How long the plate takes to arrive.</summary>
    public static double FadeInSeconds => 0.13;

    /// <summary>How long it stays up once it has arrived.</summary>
    public static double HoldSeconds => 1.5;

    /// <summary>How long it takes to leave.</summary>
    public static double FadeOutSeconds => 0.3;

    /// <summary>The whole life of one toast, and the hide timer is built from it.</summary>
    public static double VisibleMilliseconds => (FadeInSeconds + HoldSeconds + FadeOutSeconds) * 1000.0;

    public DispatcherTimer HideTimer { get; }

    /// <summary>
    /// One short line, centred at the top of the screen. The message is trimmed to
    /// a single line so a long one cannot push the plate wider than the screen.
    /// </summary>
    public void ShowToast(string message, bool warn = false)
    {
        HeadText.Text = string.IsNullOrWhiteSpace(message) ? "Gamer Tool" : message.Trim();
        Dot.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(warn ? "#F5A524" : "#2DD4BF"));
        Prepare();
        Show();
        Pulse();
    }

    private void Prepare()
    {
        UpdateLayout();

        // Centred on whichever monitor the pointer area sits on, falling back to
        // the primary screen, so a second display does not push it off screen.
        Rect area = SystemParameters.WorkArea;
        double width = ActualWidth > 0 ? ActualWidth : 320.0;
        double height = ActualHeight > 0 ? ActualHeight : 44.0;

        Left = area.Left + ((area.Width - width) / 2.0);
        Top = area.Top + 10.0;

        if (Left < area.Left)
        {
            Left = area.Left;
        }

        if (Top + height > area.Bottom)
        {
            Top = area.Bottom - height;
        }

        // Opacity is not touched here. Pulse owns it, because Pulse is the only
        // thing that has to detach the previous animation before writing to it,
        // and an assignment made here would be underneath that animation and do
        // nothing.
    }

    private void OnOsdLoaded(object sender, RoutedEventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int style = GetWindowLongPtr(handle, GwlExStyle).ToInt32();
        style |= WsExTransparent | WsExNoActivate | WsExTopmost | WsExToolWindow;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    /// <summary>
    /// Run the toast's whole life as one timeline on the window's own opacity.
    /// <para>
    /// Three things were wrong with this and all three showed up as the same
    /// symptom: a plate that flashed instead of appearing.
    /// </para>
    /// <para>
    /// The fade was attached to <c>Frame</c>, the border inside the window, while
    /// <see cref="Prepare"/> set <c>Opacity</c> on the window. Those are two
    /// different elements, so the assignment the code relied on to bring the toast
    /// back was writing a property nothing was reading.
    /// </para>
    /// <para>
    /// The timeline was also built once in the constructor and re-applied to every
    /// toast with a new <c>BeginTime</c>. Its <c>FillBehavior.HoldEnd</c> left the
    /// frame pinned at zero after the first one, and re-applying a timeline whose
    /// playhead had already run past its end evaluated it at that end
    /// immediately - measured, the second toast opened at 0.000 opacity and was
    /// already on its way out. Every toast after the first was a flash.
    /// </para>
    /// <para>
    /// And there was no fade in at all, only a fade out, so even the first toast
    /// arrived at full strength with nothing easing it onto the screen.
    /// </para>
    /// <para>
    /// So: a fresh timeline per toast, built here rather than held; the hold and
    /// both fades expressed as keyframes on one property, because WPF allows a
    /// single animation per property and two <c>BeginAnimation</c> calls would
    /// have had the second replace the first; and the previous animation detached
    /// before the opacity is written, because a HoldEnd animation owns the property
    /// and an assignment underneath it does nothing at all.
    /// </para>
    /// </summary>
    public void Pulse()
    {
        HideTimer.Stop();

        // Release the property first. Without this the HoldEnd from the last toast
        // is still attached and keeps the window at zero no matter what is
        // assigned to it.
        BeginAnimation(OpacityProperty, null);
        Opacity = 0.0;

        // KeyTime converts implicitly from TimeSpan, but an unlabelled one here reads as
        // a Percent overload picked by accident and will not survive somebody
        // tidying the arithmetic, so the conversion is written down.
        static KeyTime At(double seconds) => (KeyTime)TimeSpan.FromSeconds(seconds);

        var life = new DoubleAnimationUsingKeyFrames();
        life.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));

        var fadeIn = new EasingDoubleKeyFrame(1.0, At(FadeInSeconds));
        fadeIn.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        life.KeyFrames.Add(fadeIn);

        life.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, At(FadeInSeconds + HoldSeconds)));

        var fadeOut = new EasingDoubleKeyFrame(0.0, At(FadeInSeconds + HoldSeconds + FadeOutSeconds));
        fadeOut.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        life.KeyFrames.Add(fadeOut);

        life.FillBehavior = FillBehavior.HoldEnd;
        BeginAnimation(OpacityProperty, life, HandoffBehavior.SnapshotAndReplace);

        HideTimer.Start();
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        HideTimer.Stop();
        Hide();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
    }

    private static void SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hwnd, index, value);
        }
        else
        {
            SetWindowLong32(hwnd, index, value.ToInt32());
        }
    }
}
