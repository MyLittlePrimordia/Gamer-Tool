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

        FadeOut = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromSeconds(0.35)),
            FillBehavior = FillBehavior.HoldEnd
        };

        HideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2000)
        };
        HideTimer.Tick += OnHideTimerTick;

        Loaded += OnOsdLoaded;
    }

    public DoubleAnimation FadeOut { get; }

    public DispatcherTimer HideTimer { get; }

    public void ShowToast(string headline, string subline)
    {
        HeadText.Text = headline;
        SubText.Text = subline;
        Prepare();
        Show();
        Pulse();
    }

    private void Prepare()
    {
        double width = ActualWidth > 0 ? ActualWidth : 620;
        double left = SystemParameters.VirtualScreenLeft + ((SystemParameters.VirtualScreenWidth - width) / 2.0);
        double top = SystemParameters.VirtualScreenTop + 8;
        Left = left;
        Top = top;
        Opacity = 1.0;
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

    private void Pulse()
    {
        HideTimer.Stop();
        FadeOut.BeginTime = TimeSpan.FromSeconds(1.5);
        Frame.BeginAnimation(OpacityProperty, FadeOut, HandoffBehavior.SnapshotAndReplace);
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
