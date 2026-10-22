using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GamerTool;
using GamerTool.Models;
using GamerTool.Services;

namespace GamerTool.Tests;

/// <summary>
/// The Crosshair tab as a test drives it: the harness, plus the operations and
/// fixtures that only make sense for this tab.
/// <para>
/// Kept separate from <see cref="WindowHarness"/> so that adding a tab to the suite
/// means writing one small driver next to it, not growing the shared harness. The
/// harness owns the window, the temp profile and the teardown; this owns what the
/// crosshair tab is opened on and how its controls are pressed.
/// </para>
/// <para>
/// The own-colours switch, the Reset dialog and the pack browser all work through
/// private handlers and real routed events, so every operation here is the gesture
/// a user makes rather than a call to the method behind it.
/// </para>
/// </summary>
internal sealed class CrosshairTab
{
    /// <summary>A white mask picture, which is what the pack and most imports are.</summary>
    public const string WhitePicture = "custom-white.png";

    /// <summary>A picture with colours of its own, for the reset's own-colours rule.</summary>
    public const string ColourPicture = "custom-cyan.png";

    private readonly WindowHarness _harness;

    private CrosshairTab(WindowHarness harness) => _harness = harness;

    public MainWindow Window => _harness.Window;

    /// <summary>The window's own settings object, which is the one its handlers write to.</summary>
    public AppSettings Settings => _harness.Settings;

    public string PackFolder => _harness.PackFolder;

    /// <summary>The crosshair the tab is editing, i.e. what the overlay would draw.</summary>
    public CrosshairPreset Work => _harness.Read<CrosshairPreset>("_workCrosshair");

    /// <summary>The saved presets, in the order the dropdown lists them.</summary>
    public IReadOnlyList<CrosshairPreset> Saved => Settings.CustomCrosshairs;

    /// <summary>The preset the tab is editing; empty while it reads "Tuned".</summary>
    public string PresetId => _harness.Read<string>("_xhPresetId");

    /// <summary>The preset the dropdown is showing, which is what the user reads.</summary>
    public string ShownPresetId => WindowHarness.SelectedPresetId(Window.XhPresetBox);

    /// <summary>A private field of the window, for the state with no control behind it.</summary>
    public T Read<T>(string name) => _harness.Read<T>(name);

    public bool ModalOpen => _harness.Read<Border>("_modalLayer").Visibility == Visibility.Visible;

    public string ModalTitle => _harness.Read<TextBlock>("_modalTitle").Text;

    /// <summary>What the dialog tells the user it is about to do.</summary>
    public string ModalWarning => _harness.Read<TextBlock>("_modalWarningText").Text;

    /// <summary>How many pack tiles are playing an animation right now.</summary>
    public int AnimatedTiles => ((IList)_harness.Peek("_xhPackPlayers")!).Count;

    public bool PackClockRunning
    {
        get
        {
            var clock = (DispatcherTimer?)_harness.Peek("_xhPackClock");
            return clock?.IsEnabled == true;
        }
    }

    public int PackTiles => Window.XhPackPanel.Children.Count;

    /// <summary>
    /// Stand the tab up, run <paramref name="body"/> on it, and take it down. The
    /// window is opened on a picture crosshair tuned in every field a reset has to
    /// put back, so a reset that misses one is visible rather than invisible because
    /// it already held the default.
    /// </summary>
    public static void Run(Action<CrosshairTab> body, Action<AppSettings>? configure = null)
    {
        WindowHarness.Run(
            MainWindowTabs.Crosshair,
            harness => body(new CrosshairTab(harness)),
            setup =>
            {
                File.WriteAllBytes(Path.Combine(setup.CrosshairsFolder, WhitePicture), Png(255, 255, 255));
                File.WriteAllBytes(Path.Combine(setup.CrosshairsFolder, ColourPicture), Png(0x1F, 0xE0, 0xFF));

                var preset = new CrosshairPreset
                {
                    Id = "xh-test",
                    Name = "Test",
                    ShapeKind = CrosshairShape.Custom,
                    CustomImage = WhitePicture,
                    Color = "#FF2BB0",
                    Size = 57,
                    Thickness = 7,
                    Gap = 0,
                    PictureGap = 11,
                    Opacity = 35,
                    Rotation = 200,
                    CenterDot = true,
                    Glow = true,
                    Outline = false
                };

                setup.Settings.CustomCrosshairs.Add(preset);
                setup.Settings.ActiveCrosshairId = preset.Id;
                configure?.Invoke(setup.Settings);
            });
    }

    /// <summary>Press a button the way a mouse does, so its real handler runs.</summary>
    public static void Click(ButtonBase button) => WindowHarness.Click(button);

    /// <summary>
    /// Measure and arrange the tab's page the way showing the window would, so a test
    /// can say where a control ended up. Until this runs every control on the page
    /// reports a size of zero.
    /// <para>
    /// The page rather than the window, because a <see cref="MainWindow"/> that was
    /// never shown has no layout of its own: WPF drives a window's layout from its
    /// handle, so measuring the window lays out nothing under it. This page is in fact
    /// not attached to the window at all yet - the tab content is realised when the
    /// tab is shown - so the walk up from a control on it ends at the page's own root.
    /// </para>
    /// </summary>
    public FrameworkElement LayOut(double width = 964, double height = 700)
    {
        FrameworkElement page = Page();
        page.Measure(new Size(width, height));
        page.Arrange(new Rect(0, 0, width, height));
        page.UpdateLayout();
        return page;
    }

    /// <summary>
    /// The root element of the Crosshair tab's page, found from a control on it: the
    /// topmost visual ancestor, which is the page's own <c>PagePanel</c> border.
    /// <para>
    /// Visual ancestors and not logical ones. Before the tab has been shown its
    /// logical parents still lead up through the TabItem and the TabControl to the
    /// window, and the window is exactly the element that cannot be laid out here.
    /// </para>
    /// </summary>
    public FrameworkElement Page()
    {
        DependencyObject at = Window.XhShowButton;
        while (VisualTreeHelper.GetParent(at) is { } up)
        {
            at = up;
        }

        return (FrameworkElement)at;
    }

    /// <summary>Choose a shape by pressing its tile, which is what a user does.</summary>
    public void SelectShape(CrosshairShape shape)
    {
        RadioButton tile = Window.XhTilePanel.Children
            .OfType<RadioButton>()
            .First(rb => rb.Tag is CrosshairShape tag && tag == shape);

        Click(tile);
    }

    /// <summary>Fill the pack browser from the given files instead of the user's pack folder.</summary>
    public void FillPack(params string[] paths)
    {
        Field("_xhPackFiles").SetValue(Window, paths.ToList());
        Invoke("FillXhPack");
    }

    public Button PackTileFor(string path)
    {
        return Window.XhPackPanel.Children
            .OfType<Button>()
            .First(b => string.Equals(b.Tag as string, path, StringComparison.OrdinalIgnoreCase));
    }

    public void ClosePack() => Invoke("OnXhPackClosed", null, EventArgs.Empty);

    public void ConfirmModal() => Click(_harness.Read<Button>("_modalSaveButton"));

    public void CancelModal() => Click(_harness.Read<Button>("_modalCancelButton"));

    /// <summary>
    /// Run the dispatcher until <paramref name="condition"/> holds. The pack browser
    /// decodes on a worker thread and comes back through the dispatcher, so a test
    /// that only asserted immediately would be asserting on the still.
    /// </summary>
    public void PumpUntil(Func<bool> condition, int timeoutMs = 5000) =>
        _harness.PumpUntil(condition, timeoutMs);

    // ------------------------------------------------------------ pack pictures

    public string WritePackPng(string name, byte r = 255, byte g = 255, byte b = 255)
    {
        string path = Path.Combine(PackFolder, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Sheet(r, g, b)));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    public string WritePackGif(string name, params (byte R, byte G, byte B)[] frames)
    {
        string path = Path.Combine(PackFolder, name);
        var encoder = new GifBitmapEncoder();
        foreach ((byte r, byte g, byte b) in frames)
        {
            encoder.Frames.Add(BitmapFrame.Create(Sheet(r, g, b)));
        }

        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>An opaque square of one colour, the simplest picture a pack tile can hold.</summary>
    private static BitmapSource Sheet(byte r, byte g, byte b)
    {
        const int side = 32;
        byte[] pixels = new byte[side * side * 4];
        for (int i = 0; i < side * side; i++)
        {
            int at = i * 4;
            pixels[at] = b;
            pixels[at + 1] = g;
            pixels[at + 2] = r;
            pixels[at + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Pbgra32, null, pixels, side * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Png(byte r, byte g, byte b)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Sheet(r, g, b)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    // -------------------------------------------------------------- reflection

    /// <summary>
    /// The crosshair fields with no control behind them. Named here rather than in
    /// the harness, so the harness stays about the window and each driver fails in
    /// its own terms if one of its fields is renamed.
    /// </summary>
    private System.Reflection.FieldInfo Field(string name)
    {
        return typeof(MainWindow).GetField(
                name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow." + name + " is gone; the Crosshair tab driver needs updating.");
    }

    private void Invoke(string name, params object?[]? arguments) => _harness.Invoke(name, arguments);
}
