namespace GamerTool.Models;

/// <summary>
/// A saved display configuration: brightness/contrast/gamma plus the
/// "black equalizer" shadow boost gamers use to spot enemies in dark corners,
/// and per-channel RGB scaling for warmth/tint adjustments.
/// </summary>
public class DisplayPreset
{
    public string Name { get; set; } = "New Display Preset";

    /// <summary>-1.0 .. +1.0, additive offset applied after gamma/contrast.</summary>
    public double Brightness { get; set; } = 0.0;

    /// <summary>0.5 .. 2.0 multiplier around the midpoint (1.0 = no change).</summary>
    public double Contrast { get; set; } = 1.0;

    /// <summary>0.1 .. 4.0, standard gamma curve exponent (2.2 = typical display default).</summary>
    public double Gamma { get; set; } = 2.2;

    /// <summary>0 .. 100. Lifts shadow detail (values below mid-gray) without touching highlights.</summary>
    public double ShadowBoost { get; set; } = 0.0;

    /// <summary>0.0 .. 1.0 per-channel scale, 1.0 = unmodified.</summary>
    public double RedScale { get; set; } = 1.0;
    public double GreenScale { get; set; } = 1.0;
    public double BlueScale { get; set; } = 1.0;

    public static DisplayPreset Daylight => new()
    {
        Name = "Daylight / Competitive",
        Brightness = 0.0,
        Contrast = 1.05,
        Gamma = 2.2,
        ShadowBoost = 40,
        RedScale = 1.0,
        GreenScale = 1.0,
        BlueScale = 1.0
    };

    public static DisplayPreset NightMode => new()
    {
        Name = "Night Mode",
        Brightness = -0.08,
        Contrast = 0.95,
        Gamma = 2.0,
        ShadowBoost = 10,
        RedScale = 1.0,
        GreenScale = 0.9,
        BlueScale = 0.7
    };

    public static DisplayPreset Cinematic => new()
    {
        Name = "Cinematic",
        Brightness = 0.0,
        Contrast = 1.15,
        Gamma = 2.2,
        ShadowBoost = 0,
        RedScale = 1.0,
        GreenScale = 1.0,
        BlueScale = 1.0
    };

    public static DisplayPreset ShadowHunter => new()
    {
        Name = "Shadow Hunter",
        Brightness = 0.02,
        Contrast = 1.1,
        Gamma = 2.1,
        ShadowBoost = 70,
        RedScale = 1.0,
        GreenScale = 1.0,
        BlueScale = 1.0
    };

    public static List<DisplayPreset> Defaults => new()
    {
        Daylight, NightMode, Cinematic, ShadowHunter
    };
}
