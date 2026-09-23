using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GamerTool.UI;

/// <summary>
/// A frameless, click-through, always-on-top toast shown briefly in the
/// top-right corner when a hotkey fires — so the user gets confirmation
/// without alt-tabbing out of their game. Click-through is implemented via
/// WS_EX_TRANSPARENT so it never steals mouse/keyboard focus from the game.
/// </summary>
public partial class OsdNotification : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    public OsdNotification(string message)
    {
        InitializeComponent();
        MessageText.Text = message;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
        };
    }

    /// <summary>Shows the toast in the top-right corner of the primary screen
    /// for ~1.5s, then closes itself. Fire-and-forget from the UI thread.</summary>
    public static void ShowToast(string message)
    {
        var toast = new OsdNotification(message);
        toast.Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            toast.Left = workArea.Right - toast.ActualWidth - 24;
            toast.Top = workArea.Top + 24;
        };
        toast.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            toast.Close();
        };
        timer.Start();
    }
}
