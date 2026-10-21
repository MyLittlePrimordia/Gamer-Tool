using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GamerTool.Services;
namespace GamerTool.UI;

public partial class RetroOsd : Window
{
    private const int GwlExStyle = -20;

    private const int WsExTransparent = 0x00000020;

    private const int WsExNoActivate = 0x08000000;

    private const int WsExTopmost = 0x00000008;

    private const int WsExToolWindow = 0x00000080;

    private const uint SwpNoSize = 0x0001;

    private const uint SwpNoZOrder = 0x0004;

    private const uint SwpNoActivate = 0x0010;

    /// <summary>The window handle, once there is one. Zero before the first show.</summary>
    private IntPtr _handle;

    /// <summary>
    /// The work area this toast was asked to use, or null for "whichever screen the
    /// foreground window is on".
    /// <para>
    /// Asked for per toast rather than held, because the caller that knows better
    /// than the foreground window is the one starting a game: Play knows the screen
    /// the slot is keyed to, and at that moment the foreground window is still this
    /// app or a launcher, not the game.
    /// </para>
    /// </summary>
    private Rect? _requestedScreen;

    /// <summary>The work area actually in use, in physical pixels.</summary>
    private Rect _screen;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
    /// One short line, centred at the top of a screen. The message is trimmed to
    /// a single line so a long one cannot push the plate wider than the screen.
    /// </summary>
    /// <param name="screen">
    /// The work area, in physical pixels, to put it on. Null means the screen the
    /// foreground window is on, which is the game whenever a hotkey or an automatic
    /// slot load is what caused the toast.
    /// </param>
    public void ShowToast(string message, bool warn = false, Rect? screen = null)
    {
        HeadText.Text = string.IsNullOrWhiteSpace(message) ? "Gamer Tool" : message.Trim();
        Dot.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(warn ? "#F5A524" : "#2DD4BF"));

        _requestedScreen = screen;
        Prepare();

        Show();

        // Placed a second time now that the window exists and the plate has been
        // measured: the first placement above runs before there is a handle to move
        // on the very first toast, and before the text has been laid out on every
        // one. This runs before Pulse makes the plate visible, so the approximate
        // position is never painted.
        UpdateLayout();
        Place();

        Pulse();
    }

    private void Prepare()
    {
        UpdateLayout();

        // Which screen, asked for again on every toast: a game taking over another
        // monitor, a display being unplugged or a dock being connected all change
        // the answer, and all of them can happen between two toasts.
        _screen = _requestedScreen ?? MonitorWorkArea.ForForeground();

        Place();

        // Opacity is not touched here. Pulse owns it, because Pulse is the only
        // thing that has to detach the previous animation before writing to it,
        // and an assignment made here would be underneath that animation and do
        // nothing.
    }

    /// <summary>
    /// Moves the plate into its place on <see cref="_screen"/>, in physical pixels.
    /// <para>
    /// Straight to pixels with <c>SetWindowPos</c>, the same as the crosshair
    /// overlay and for the same reason: <c>Left</c> and <c>Top</c> are device
    /// independent units of whichever screen the window was last on, which is the
    /// wrong ruler for putting a window on an exact pixel of a screen with a
    /// different scale. The arithmetic itself is in <see cref="ToastPlacement"/>,
    /// where it can be tested without a desktop.
    /// </para>
    /// <para>
    /// The size is read back from the window rather than from
    /// <c>ActualWidth</c>/<c>ActualHeight</c>, which are device independent units
    /// and would put the plate off centre by the scale factor on any screen that is
    /// not at 100%.
    /// </para>
    /// </summary>
    private void Place()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        if (!GetWindowRect(_handle, out NativeRect rect))
        {
            return;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        (int x, int y) = ToastPlacement.TopLeft(
            _screen,
            width,
            height,
            ToastPlacement.TopMargin * dpi.DpiScaleY);

        // Topmost is not re-asserted here: the window is created topmost and
        // SWP_NOZORDER leaves the z-order alone, so it stays above the game.
        SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>
    /// Windows resizes a window to keep it the same size in device independent
    /// units when it crosses onto a screen with a different scale, so its pixel
    /// width changes and the centring has to be done over.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // After the layout pass, so the size that gets measured is the new one.
        Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
    }

    private void OnOsdLoaded(object sender, RoutedEventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _handle = handle;

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
    /// the code that reset it wrote <c>Opacity</c> on the window. Those are two
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

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
