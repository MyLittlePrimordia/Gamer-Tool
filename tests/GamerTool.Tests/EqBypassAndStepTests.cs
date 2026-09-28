using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Two things about the equaliser that are easy to get quietly wrong.
/// <para>
/// The band step is a quarter of a decibel, so every place a band value is turned
/// into text has to carry two decimals. One decimal does not merely round the
/// display: the live push and the preset file are both text, and at one decimal
/// the engine was sent 0.2 while the file said 0.25, which came back as a drift
/// report about a value that had never actually disagreed.
/// </para>
/// <para>
/// The preset file also carries an on/off flag for the equaliser, and this file
/// used to claim that flag was how the bypass worked, because that is what the
/// engine's own presets do with it. It is not. The engine ignores the field when
/// it loads a preset, and the bypass works by sending flat gains in a
/// <c>--set_band_gain</c> command of its own. That was confirmed against a running
/// FxSound 1.2.15: rewriting the flag changed nothing the engine reported, while
/// the flat gains landed immediately. The flag is still written to match the
/// effect being asked for, so these tests hold, but they are assertions about the
/// file's contents and not about the mechanism. The mechanism is covered in
/// <see cref="EqBypassCommandTests"/>.
/// </para>
/// </summary>
public class EqBypassAndStepTests
{
    private static AudioPreset Tune(params double[] bands) => new()
    {
        Name = "GamerTool",
        Bands = bands,
        NumBands = bands.Length,
    };

    private static string WriteAndRead(AudioPreset preset, bool effectsEnabled)
    {
        string path = Path.Combine(Path.GetTempPath(), "GamerToolTests-" + System.Guid.NewGuid().ToString("N")[..8] + ".fac");
        try
        {
            string? written = FxPresetFile.Write(preset, Enumerable.Range(0, preset.NumBands).Select(i => AudioPreset.BandFrequency(preset.NumBands, i)).ToList(), effectsEnabled);
            Assert.NotNull(written);
            return File.ReadAllText(written!);
        }
        finally
        {
            foreach (string f in Directory.GetFiles(Path.GetTempPath(), "GamerToolTests-*.fac"))
            {
                try
                {
                    File.Delete(f);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public void A_quarter_decibel_survives_the_live_push_unchanged()
    {
        // The one that matters most: this is the string handed to the engine.
        string wire = AudioService.BandString(new[] { -12.0, -0.25, 0.0, 0.25, 9.25, 12.0 });

        Assert.Equal("0:-12.00,1:-0.25,2:0.00,3:0.25,4:9.25,5:12.00", wire);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(-0.25)]
    [InlineData(9.25)]
    [InlineData(-11.75)]
    public void A_quarter_decibel_survives_the_preset_file_unchanged(double gain)
    {
        string text = WriteAndRead(Tune(gain), effectsEnabled: true);

        // The file writes "   x: Boost/Cut", so match on the value being present
        // rather than on its exact column, which is not part of the contract.
        Assert.Contains(gain.ToString("0.##", CultureInfo.InvariantCulture) + ": Boost/Cut", text);
    }

    [Fact]
    public void The_preset_file_records_the_equaliser_as_on_by_default()
    {
        string text = WriteAndRead(Tune(3.0), effectsEnabled: true);

        Assert.Contains("1: On/Off Flag", text);
    }

    [Fact]
    public void Bypassing_the_equaliser_writes_a_zero_into_the_presets_own_flag()
    {
        // What this asserts is the file's contents, which is worth pinning down so
        // the file cannot quietly come to describe the opposite of what the app
        // did. It is not the mechanism: the engine ignores this field on load, and
        // the bypass works by sending flat gains in a --set_band_gain command of
        // its own. See the note on the class.
        string text = WriteAndRead(Tune(3.0), effectsEnabled: false);

        Assert.Contains("0: On/Off Flag", text);
        Assert.DoesNotContain("1: On/Off Flag", text);
    }

    [Fact]
    public void Bypassing_leaves_the_curve_in_the_file()
    {
        // A bypass is not a reset. Someone who switches the equaliser off to hear
        // the un-tuned signal still wants their curve back when they switch it on,
        // so the gains have to survive being written with the flag at zero.
        string text = WriteAndRead(Tune(6.0, -4.0, 2.5), effectsEnabled: false);

        Assert.Contains("0: On/Off Flag", text);
        Assert.Contains("6: Boost/Cut", text);
        Assert.Contains("-4: Boost/Cut", text);
        Assert.Contains("2.5: Boost/Cut", text);
    }

    [Fact]
    public void Both_states_write_the_same_number_of_bands()
    {
        string on = WriteAndRead(Tune(1.0, 2.0, 3.0, 4.0, 5.0), effectsEnabled: true);
        string off = WriteAndRead(Tune(1.0, 2.0, 3.0, 4.0, 5.0), effectsEnabled: false);

        Assert.Equal(on.Split('\n').Count(l => l.Contains("Boost/Cut", StringComparison.Ordinal)),
                     off.Split('\n').Count(l => l.Contains("Boost/Cut", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_new_profile_has_the_effects_enabled()
    {
        // A fresh install must not come up muted. Processing on is the default, so a
        // user who has never touched the BYPASS switch gets their curve.
        Assert.True(new AppSettings().EffectsEnabled);
    }

    [Fact]
    public void A_new_profile_shows_the_bypass_switch_off()
    {
        // The same default, stated the way the user sees it. The switch is labelled
        // BYPASS, so a default that has the effects enabled must draw the switch
        // unengaged, or the app opens claiming to be silenced while it is not.
        Assert.False(BypassToggle.IsEngaged(new AppSettings().EffectsEnabled));
    }

    [Fact]
    public void A_restored_profile_keeps_the_bypass_the_user_chose()
    {
        // The flag lives in settings, so it rides through a backup and a restore
        // like any other preference. Restoring a profile that was saved with the
        // chain bypassed must not quietly turn it back on.
        string json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { EffectsEnabled = false });
        AppSettings? back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(back);
        Assert.False(back!.EffectsEnabled);

        // And the switch has to come back showing that, not the other way round.
        Assert.True(BypassToggle.IsEngaged(back!.EffectsEnabled));
    }
}
