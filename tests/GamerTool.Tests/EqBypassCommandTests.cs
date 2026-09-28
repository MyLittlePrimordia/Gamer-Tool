using System;
using System.Globalization;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// What the equaliser bypass actually puts on the wire.
/// <para>
/// This went wrong three times before it worked, and every failure was invisible
/// in the command string, which is why the shape of the apply is asserted here
/// as well as its contents.
/// </para>
/// <para>
/// First: writing a zero into the preset file's own "On/Off Flag" looks like the
/// answer and does nothing, because the engine ignores that field on load.
/// FxSound's command line has no equaliser on/off option among its twenty-one
/// flags, so the gains are the only way to say it.
/// </para>
/// <para>
/// Second: sending the flattened gains in the same command line as the preset
/// does nothing either. The engine applies a selected preset's band gains
/// <em>after</em> it finishes parsing, so position in the argument list is
/// irrelevant and the curve always came back. Verified on FxSound 1.2.15 by
/// replaying both shapes against the running engine: one invocation returned the
/// curve, two returned flat. So the gains go out as a second command.
/// </para>
/// <para>
/// Third, and the reason the second fix did not take either: the arguments were
/// handed over with every quote rewritten as \" , which stops the engine reading
/// a band list after its first entry. Covered at the bottom of this file.
/// </para>
/// </summary>
public class EqBypassCommandTests
{
    private static AudioService Service(bool effectsEnabled)
    {
        // Anti-clip is pinned off so master gain passes straight through. It has
        // its own tests and its own headroom arithmetic, and leaving it on would
        // mean these assertions were really measuring that instead.
        //
        // The parameter is named for the setting, not for the switch: the setting
        // says whether the chain is live, the BYPASS switch says whether it is
        // silenced, and the two are opposites. It used to be called bypassOn and
        // took the same values, so every call site below read "bypass off" for a
        // state that was actually the bypass engaged.
        return new AudioService { EffectsEnabled = effectsEnabled, AntiClipEnabled = false };
    }

    private static AudioPreset Tuned()
    {
        double[] bands = { -6.25, 0.25, 3.0, 0.0, 12.0, -4.0, 9.25, 1.5, -0.75, 6.0 };
        return new AudioPreset { Name = FxPresetFile.PresetName, Bands = bands, NumBands = bands.Length };
    }

    /// <summary>Pulls the band gain list back out of a command.</summary>
    private static double[] GainsIn(string command)
    {
        const string flag = "--set_band_gain=\"";
        int start = command.IndexOf(flag, StringComparison.Ordinal);
        Assert.True(start >= 0, "the command carried no band gains: " + command);
        start += flag.Length;
        int end = command.IndexOf('"', start);
        Assert.True(end > start, "the band gains were not terminated: " + command);
        return command.Substring(start, end - start)
            .Split(',')
            .Select(pair => double.Parse(pair.Split(':')[1], CultureInfo.InvariantCulture))
            .ToArray();
    }

    [Fact]
    public void A_bypassed_equaliser_reaches_the_engine_as_a_flat_curve()
    {
        double[] gains = GainsIn(Service(effectsEnabled: false).BuildBypassCommand(Tuned()));

        Assert.Equal(AudioPreset.PresetBandCount, gains.Length);
        Assert.All(gains, g => Assert.Equal(0.0, g));
    }

    [Fact]
    public void An_enabled_equaliser_still_reaches_the_engine_as_the_built_curve()
    {
        AudioPreset preset = Tuned();
        double[] gains = GainsIn(Service(effectsEnabled: true).BuildBypassCommand(preset));

        Assert.Equal(preset.Bands.Length, gains.Length);
        for (int i = 0; i < preset.Bands.Length; i++)
        {
            Assert.Equal(preset.Bands[i], gains[i], 2);
        }
    }

    [Fact]
    public void Turning_the_bypass_on_and_off_is_the_same_curve_either_way()
    {
        // The promise of holding the switch: what you built survives a trip
        // through off and comes back exactly as it was.
        AudioPreset preset = Tuned();
        Service(effectsEnabled: false).BuildBypassCommand(preset);
        double[] afterOff = GainsIn(Service(effectsEnabled: true).BuildBypassCommand(preset));

        for (int i = 0; i < preset.Bands.Length; i++)
        {
            Assert.Equal(preset.Bands[i], afterOff[i], 2);
        }
    }

    [Fact]
    public void A_bypass_does_not_disturb_the_preset_it_was_given()
    {
        // Flattening the copy is the whole trick. Were it done on the preset, the
        // user's curve would be destroyed by the act of switching it off.
        AudioPreset preset = Tuned();
        double[] before = preset.Bands.ToArray();

        Service(effectsEnabled: false).BuildBypassCommand(preset);

        Assert.Equal(before, preset.Bands);
    }

    [Fact]
    public void The_main_apply_does_not_carry_the_band_gains()
    {
        // The regression that hid for so long. The gains were in this command,
        // written after the preset so they looked right, and were discarded
        // because the engine applies the preset after parsing the whole line.
        string main = Service(effectsEnabled: false).BuildApplyCommands(Tuned(), "Speakers")[0];

        Assert.DoesNotContain("--set_band_gain", main);
    }

    [Fact]
    public void An_apply_is_two_commands_with_the_gains_in_the_second()
    {
        // The shape of the fix. One command for the engine and the preset, then
        // a second one for the gains, because that is the only arrangement the
        // engine actually honours.
        var commands = Service(effectsEnabled: false).BuildApplyCommands(Tuned(), "Speakers");

        Assert.Equal(2, commands.Count);
        Assert.Contains("--preset=" + FxPresetFile.PresetName, commands[0]);
        Assert.Contains("--set_band_gain", commands[1]);
    }

    [Fact]
    public void The_preset_is_still_selected_so_the_file_is_picked_up()
    {
        // Both halves are load bearing. Without the preset the engine would never
        // see the file, and a restore or a new tune would leave it holding
        // whatever this process last happened to send.
        var commands = Service(effectsEnabled: true).BuildApplyCommands(Tuned(), "Speakers");

        Assert.Contains("--preset=" + FxPresetFile.PresetName, commands[0]);
        Assert.Contains("--power=1", commands[0]);
        Assert.Contains("--set_band_freq=", commands[0]);
        Assert.Contains("--set_effect=", commands[0]);
    }

    [Fact]
    public void A_bypass_leaves_the_rest_of_the_chain_alone()
    {
        // The other effects are not the equaliser and must not be dragged off
        // with it. A user bypassing a curve still wants their level and balance.
        // Values sit on each control's own step: master gain and balance move a
        // whole decibel at a time, leveling in halves.
        AudioPreset preset = Tuned();
        preset.MasterGain = -4.0;
        preset.Balance = 3.0;
        preset.VolumeLeveling = 1.5;

        string off = Service(effectsEnabled: false).BuildApplyCommands(preset, "Speakers")[0];
        string on = Service(effectsEnabled: true).BuildApplyCommands(preset, "Speakers")[0];

        foreach (string flag in new[] { "--master_gain=-4", "--balance=3", "--volume_leveling=1.5" })
        {
            Assert.Contains(flag, off);
            Assert.Contains(flag, on);
        }
    }

    [Fact]
    public void No_command_carries_a_backslash_escaped_quote()
    {
        // The quoting bug that made everything above moot. Arguments were handed
        // to the engine with every quote rewritten as an escaped one, on the
        // theory that it wanted literal quote characters. It does not: the
        // backslashes travel with the value and the engine stops reading at the
        // first one, so a ten band list arrived as a single band and a device
        // name came back from status.json as a lone backslash. Both verified
        // against FxSound 1.2.15 from an identical starting state. No value can
        // legitimately contain a quote, so this must never reappear.
        AudioPreset preset = Tuned();
        preset.MasterGain = -4.0;

        foreach (bool enabled in new[] { true, false })
        {
            foreach (string command in Service(enabled).BuildApplyCommands(preset, "Speakers (Realtek)"))
            {
                Assert.DoesNotContain("\\\"", command);
            }
        }
    }

    [Fact]
    public void A_bypassed_band_is_expected_to_read_flat_rather_than_to_be_skipped()
    {
        // The read-back and the command have to agree about what a bypassed band
        // should be, so both come from ExpectedBandGain. When the bypass used to
        // be skipped entirely, a switch that had quietly done nothing passed
        // unremarked - which is close to what happened.
        AudioPreset preset = Tuned();
        AudioService off = Service(effectsEnabled: false);

        for (int i = 0; i < preset.Bands.Length; i++)
        {
            Assert.Equal(0.0, off.ExpectedBandGain(preset, i));
        }
    }

    [Fact]
    public void An_enabled_band_is_expected_to_read_as_the_preset_built_it()
    {
        AudioPreset preset = Tuned();
        AudioService on = Service(effectsEnabled: true);

        for (int i = 0; i < preset.Bands.Length; i++)
        {
            Assert.Equal(preset.Bands[i], on.ExpectedBandGain(preset, i), 6);
        }
    }

    [Fact]
    public void What_a_band_is_expected_to_read_as_is_what_gets_sent()
    {
        // One source of truth. If these could drift apart the read-back would end
        // up grading the engine against a curve nobody asked it to play.
        AudioPreset preset = Tuned();

        foreach (bool enabled in new[] { true, false })
        {
            AudioService service = Service(enabled);
            double[] sent = GainsIn(service.BuildBypassCommand(preset));

            for (int i = 0; i < sent.Length; i++)
            {
                Assert.Equal(service.ExpectedBandGain(preset, i), sent[i], 6);
            }
        }
    }

    [Fact]
    public void The_bypass_push_carries_the_bands_and_the_effects_and_nothing_else()
    {
        // It is meant to feel like a switch, so it must not be a small apply. A
        // full apply would commit the preset file, the master gain and the output
        // device, and a fader the user had moved but not applied would be
        // committed by an unrelated click. The bands and the effects are the whole
        // of what it is allowed to touch.
        string push = Service(effectsEnabled: false).BuildBypassCommand(Tuned());

        Assert.StartsWith("--set_band_gain=", push);
        Assert.Contains("--set_effect=", push);

        foreach (string forbidden in new[]
        {
            "--power", "--preset", "--set_band_freq",
            "--master_gain", "--volume_leveling", "--filter_q", "--balance", "--output", "--num_bands",
        })
        {
            Assert.DoesNotContain(forbidden, push);
        }
    }

    [Fact]
    public void A_thrown_bypass_sends_every_effect_to_zero_as_well_as_the_bands()
    {
        // The correction. This used to flatten the bands and leave Clarity, Bass
        // and the rest running, while the switch beside them read "EQ" and off.
        AudioPreset preset = Tuned();
        preset.Clarity = 7.0;
        preset.Ambience = 4.0;
        preset.Surround = 5.0;
        preset.DynamicBoost = 6.0;
        preset.BassBoost = 8.0;

        string push = Service(effectsEnabled: false).BuildBypassCommand(preset);
        string effects = push.Substring(push.IndexOf("--set_effect=", StringComparison.Ordinal));

        Assert.Contains("clarity:0.0", effects);
        Assert.Contains("ambience:0.0", effects);
        Assert.Contains("surround:0.0", effects);
        Assert.Contains("dynamicboost:0.0", effects);
        Assert.Contains("bass:0.0", effects);

        // And no trace of what the user actually set.
        foreach (string real in new[] { "clarity:7", "ambience:4", "surround:5", "dynamicboost:6", "bass:8" })
        {
            Assert.DoesNotContain(real, effects);
        }
    }

    [Fact]
    public void Releasing_the_bypass_puts_every_effect_back()
    {
        AudioPreset preset = Tuned();
        preset.Clarity = 7.0;
        preset.Ambience = 4.0;
        preset.Surround = 5.0;
        preset.DynamicBoost = 6.0;
        preset.BassBoost = 8.0;

        IReadOnlyList<double> wanted = Service(effectsEnabled: true).ExpectedEffectValues(preset);

        Assert.Equal(new[] { 7.0, 4.0, 5.0, 6.0, 8.0 }, wanted);
    }

    [Fact]
    public void Throwing_the_bypass_never_touches_the_preset_it_was_given()
    {
        // Nothing is destroyed. The bypass only changes what is sent to the engine,
        // so every setting is still there to come back to, including band moves
        // that have not been applied.
        AudioPreset preset = Tuned();
        preset.Clarity = 7.0;
        preset.BassBoost = 8.0;
        double[] bands = preset.Bands.ToArray();

        Service(effectsEnabled: false).BuildBypassCommand(preset);

        Assert.Equal(bands, preset.Bands);
        Assert.Equal(7.0, preset.Clarity);
        Assert.Equal(8.0, preset.BassBoost);
    }

    [Fact]
    public void An_apply_while_bypassed_stays_bypassed()
    {
        // The full apply sends the effects too, so if the bypass were ignored
        // there, applying a preset would have quietly switched the bypass back off
        // without the switch ever moving. Both halves of the apply have to agree.
        AudioPreset preset = Tuned();
        preset.Clarity = 7.0;

        foreach (string command in Service(effectsEnabled: false).BuildApplyCommands(preset, "Speakers"))
        {
            if (command.Contains("--set_effect=", StringComparison.Ordinal))
            {
                Assert.Contains("clarity:0.0", command);
            }
        }
    }

    [Fact]
    public void What_an_effect_is_expected_to_read_as_is_what_gets_sent()
    {
        // One source of truth again, now for the effects as well as the bands. If
        // these could drift apart the read-back would grade the engine against
        // values nobody asked it to play.
        AudioPreset preset = Tuned();
        preset.Clarity = 7.0;
        preset.Ambience = 4.0;
        preset.Surround = 5.0;
        preset.DynamicBoost = 6.0;
        preset.BassBoost = 8.0;

        foreach (bool effectsEnabled in new[] { true, false })
        {
            AudioService service = Service(effectsEnabled);
            IReadOnlyList<double> wanted = service.ExpectedEffectValues(preset);
            string sent = service.EffectString(preset);

            for (int i = 0; i < wanted.Count; i++)
            {
                string name = sent.Split(',').First(p => p.StartsWith(NameOf(i), StringComparison.Ordinal));
                Assert.Equal(wanted[i].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), name.Split(':')[1]);
            }
        }
    }

    private static string NameOf(int index) => new[] { "clarity", "ambience", "surround", "dynamicboost", "bass" }[index];
}
