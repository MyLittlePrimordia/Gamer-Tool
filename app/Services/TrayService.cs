using System;
using System.Windows.Forms;

namespace GamerTool.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;

    private bool _disposed;

    public event Action? ShowRequested;

    public event Action? ResetScreenRequested;

    public event Action? ResetSoundRequested;

    public event Action? QuitRequested;

    public TrayService()
    {
        ToolStripMenuItem show = new("Open Gamer Tool");
        show.Click += (s, e) => ShowRequested?.Invoke();

        ToolStripMenuItem resetScreen = new("Reset screen");
        resetScreen.Click += (s, e) => ResetScreenRequested?.Invoke();

        ToolStripMenuItem resetSound = new("Reset sound");
        resetSound.Click += (s, e) => ResetSoundRequested?.Invoke();

        ToolStripMenuItem quit = new("Quit Gamer Tool");
        quit.Click += (s, e) => QuitRequested?.Invoke();

        ContextMenuStrip menu = new();
        menu.Items.Add(show);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(resetScreen);
        menu.Items.Add(resetSound);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);

        _icon = new NotifyIcon
        {
            Icon = IconFactory.CreateRuntimeIcon(32),
            Text = "Gamer Tool",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (s, e) => ShowRequested?.Invoke();
    }

    public void SetStatus(string text)
    {
        string clean = text.Length > 63 ? text.Substring(0, 63) : text;
        _icon.Text = "Gamer Tool - " + clean;
    }

    public void Balloon(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(3000);
        }
        catch (Exception ex)
        {
            TraceLog.Write("TRAY", ex);
        }
    }

    public void Hide()
    {
        _icon.Visible = false;
    }

    public void Show()
    {
        _icon.Visible = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
