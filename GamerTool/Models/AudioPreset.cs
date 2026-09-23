namespace GamerTool.Models;

/// <summary>
/// A saved 10-band EQ configuration matching the Equalizer APO band layout
/// (31/62/125/250/500/1k/2k/4k/8k/16k Hz), plus preamp and anti-clip.
/// </summary>
public class AudioPreset
{
    public static readonly int[] Frequencies =
        { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public string Name { get; set; } = "New Audio Preset";

    /// <summary>dB gain per band, -15..+15, same order as Frequencies.</summary>
    public double[] Bands { get; set; } = new double[10];

    /// <summary>Master preamp in dB, -20..+15.</summary>
    public double Preamp { get; set; } = 0.0;

    /// <summary>If true, preamp is auto-reduced so peak band+preamp never exceeds 0 dB.</summary>
    public bool AntiClip { get; set; } = true;

    public static AudioPreset Flat => new()
    {
        Name = "Flat",
        Bands = new double[10],
        Preamp = 0.0,
        AntiClip = true
    };

    public static AudioPreset FpsShooter => new()
    {
        Name = "FPS Shooter",
        // 31,  62,  125, 250, 500, 1k,  2k,  4k,  8k,  16k
        Bands = new double[] { -4, -4, -2, 0, 0, 2, 5, 5, 2, 0 },
        Preamp = 0.0,
        AntiClip = true
    };

    public static AudioPreset StoryCinematic => new()
    {
        Name = "Story / Cinematic",
        Bands = new double[] { 4, 4, 2, 0, 0, 0, 1, 2, 3, 3 },
        Preamp = -1.0,
        AntiClip = true
    };

    public static AudioPreset Racing => new()
    {
        Name = "Racing",
        Bands = new double[] { 2, 4, 4, 3, 1, 0, 0, 1, 1, 0 },
        Preamp = -1.0,
        AntiClip = true
    };

    public static AudioPreset ArcadeFighting => new()
    {
        Name = "Arcade / Fighting",
        Bands = new double[] { 0, 1, 2, 3, 3, 2, 1, 1, 0, 0 },
        Preamp = -1.0,
        AntiClip = true
    };

    public static List<AudioPreset> Defaults => new()
    {
        Flat, FpsShooter, StoryCinematic, Racing, ArcadeFighting
    };
}
