using System.Runtime.InteropServices;
using GamerTool.Models;

namespace GamerTool.Services;

/// <summary>
/// Applies display tuning (brightness, contrast, gamma, black-equalizer shadow
/// boost, RGB channel scaling) purely through the Win32 GDI gamma ramp API.
/// This is entirely user-mode, needs zero drivers and zero admin rights, and
/// works instantly on any monitor whose driver honors SetDeviceGammaRamp
/// (virtually all do). This is the ONLY subsystem guaranteed to work with no
/// setup at all, which is why it's the fallback-safe default.
/// </summary>
public static class DisplayService
{
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
    }

    private static RAMP? _originalRamp;

    /// <summary>Cache the ramp Windows had before GamerTool touched anything, so
    /// "Reset Display to Default" can restore it exactly rather than guessing.</summary>
    public static void CaptureOriginalRamp()
    {
        if (_originalRamp != null) return;
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            var ramp = NewRamp();
            if (GetDeviceGammaRamp(hdc, ref ramp))
                _originalRamp = ramp;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    public static void RestoreOriginalRamp()
    {
        if (_originalRamp is not { } ramp) return;
        IntPtr hdc = GetDC(IntPtr.Zero);
        try { SetDeviceGammaRamp(hdc, ref ramp); }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    private static RAMP NewRamp() => new()
    {
        Red = new ushort[256],
        Green = new ushort[256],
        Blue = new ushort[256]
    };

    /// <summary>
    /// Builds and applies a gamma ramp for the given preset. Math notes:
    ///  - gamma curve:      v = (i/255) ^ (1/gamma)
    ///  - shadow boost:     lifts v for i below mid-gray, tapering to 0 lift by i=128,
    ///                      so highlights (i > ~190) are never touched -> no washed-out whites.
    ///  - contrast:         scales around the 0.5 midpoint.
    ///  - brightness:       additive offset applied last, before clamping.
    ///  - RGB scale:        per-channel multiplier applied after everything else.
    /// </summary>
    public static void Apply(DisplayPreset preset)
    {
        CaptureOriginalRamp();

        var ramp = NewRamp();
        double gamma = Math.Max(preset.Gamma, 0.1);
        double contrast = preset.Contrast <= 0 ? 0.01 : preset.Contrast;

        for (int i = 0; i < 256; i++)
        {
            double normalized = i / 255.0;

            double v = Math.Pow(normalized, 1.0 / gamma);

            if (preset.ShadowBoost > 0 && normalized < 0.5)
            {
                double taper = 1.0 - (normalized * 2.0); // 1.0 at black, 0.0 at mid-gray
                double lift = taper * (preset.ShadowBoost / 100.0) * 0.30;
                v = Math.Clamp(v + lift, 0.0, 1.0);
            }

            v = ((v - 0.5) * contrast) + 0.5 + preset.Brightness;
            v = Math.Clamp(v, 0.0, 1.0);

            ramp.Red[i] = (ushort)Math.Clamp(v * preset.RedScale * 65535.0, 0, 65535);
            ramp.Green[i] = (ushort)Math.Clamp(v * preset.GreenScale * 65535.0, 0, 65535);
            ramp.Blue[i] = (ushort)Math.Clamp(v * preset.BlueScale * 65535.0, 0, 65535);
        }

        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (!SetDeviceGammaRamp(hdc, ref ramp))
            {
                // Some GPU drivers reject ramps that deviate too far from linear in one
                // step (a safety clamp). Falling back to identity + warning is safer
                // than leaving the display in a half-applied state.
                throw new InvalidOperationException(
                    "The display driver rejected the gamma ramp. Try a less extreme preset " +
                    "(large brightness/contrast swings can trip driver-side safety limits).");
            }
            LastApplied = preset;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>Currently active preset, so FocusWatcher knows what to re-assert.</summary>
    public static DisplayPreset? LastApplied { get; private set; }

    public static void ResetToIdentity()
    {
        RestoreOriginalRamp();
        LastApplied = null;
    }
}
