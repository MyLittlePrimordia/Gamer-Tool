using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// A reset that leaves the equaliser where the game had it.
/// <para>
/// The symptom is the worst kind to chase: the reset runs, logs SOUND RESET,
/// flattens master gain, levelling, filter Q, balance and every effect, and the
/// ten band EQ from the game is still on the engine. It reports success and does
/// not sound like one.
/// </para>
/// <para>
/// The cause is in how the engine is driven. Band gains cannot travel on the apply
/// command line, because selecting a preset makes the engine apply that file's
/// state <em>after</em> it finishes parsing the line - so anything bundled in is
/// discarded. That is why <c>Apply</c> writes the curve to a preset file and then
/// sends the gains as a second command of their own. A reset that sent only the
/// apply command was therefore selecting whatever <c>GamerTool.fac</c> last held,
/// and the last thing to write it was the game's tune.
/// </para>
/// </summary>
public class SoundResetFlattensTests
{
    [Fact]
    public void A_reset_sends_the_band_gains()
    {
        // The defect itself. Without this command the gains are never written, so
        // whatever the engine is holding survives the reset.
        AudioService audio = new();

        IReadOnlyList<string> commands = audio.BuildApplyCommands(AudioPreset.Flat(10), string.Empty, selectPresetFile: true);

        Assert.Contains(commands, c => c.Contains("--set_band_gain", StringComparison.Ordinal));
    }

    [Fact]
    public void The_gains_a_reset_sends_are_flat()
    {
        // Zero, not "unchanged". A reset that sent the previous tune's gains would
        // be worse than one that sent none.
        AudioService audio = new();

        IReadOnlyList<string> commands = audio.BuildApplyCommands(AudioPreset.Flat(10), string.Empty, selectPresetFile: true);

        string gains = commands.First(c => c.Contains("--set_band_gain", StringComparison.Ordinal));

        // Between the quotes that follow the marker. Scoped to that pair rather
        // than the first and last quote on the line, because the command carries
        // two quoted things and the effects payload is the second.
        const string Marker = "--set_band_gain=\"";
        int open = gains.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(open >= 0, "the gains were not quoted: " + gains);

        open += Marker.Length;
        int close = gains.IndexOf('"', open);
        Assert.True(close > open, "the gains were not quoted: " + gains);

        string[] entries = gains[open..close].Split(',');

        Assert.Equal(10, entries.Length);
        foreach (string entry in entries)
        {
            Assert.Equal("0.00", entry.Split(':')[1]);
        }
    }

    [Fact]
    public void A_reset_gains_carry_the_effects_too()
    {
        // The second command is the bypass command, and it carries the effects. So
        // a reset that only sent the apply command also never put the effects
        // back - it just happened to look like it did because the apply command
        // carries those separately as well.
        AudioService audio = new();

        IReadOnlyList<string> commands = audio.BuildApplyCommands(AudioPreset.Flat(10), string.Empty, selectPresetFile: true);

        Assert.Contains(commands, c => c.Contains("--set_effect", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reset_is_two_commands_not_one()
    {
        // The count is the shape of the fix: apply, then gains. One command means
        // the gains went nowhere.
        AudioService audio = new();

        IReadOnlyList<string> commands = audio.BuildApplyCommands(AudioPreset.Flat(10), string.Empty, selectPresetFile: true);

        Assert.Equal(2, commands.Count);
    }

    [Fact]
    public void The_curve_is_written_before_it_is_selected()
    {
        // The other half, and the half that is easy to believe is covered by the
        // first. Sending flat gains as a command is not enough on its own: the
        // apply command still selects GamerTool.fac, and if the file on disk is
        // the game's curve the engine applies that over the top of the gains just
        // sent. So the write has to happen, and it has to happen first.
        AudioService audio = new();

        // A folder of its own, so this never writes into the developer's or the
        // user's real FxSound presets.
        string folder = Path.Combine(
            Path.GetTempPath(),
            "GamerToolTests",
            "fx-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            AudioPreset flat = AudioPreset.Flat(10);

            bool wrote = FxPresetFile.Write(flat, AudioService.BandFrequencies(flat), true, folder) is not null;
            Assert.True(wrote, "the flat curve could not be written");

            // Written, so selecting it is safe.
            IReadOnlyList<string> selected = audio.BuildApplyCommands(flat, string.Empty, wrote);
            Assert.Contains(selected, c => c.Contains("--preset=GamerTool", StringComparison.Ordinal));
        }
        finally
        {
            FxPresetFile.RemoveWrittenPreset(folder);
        }
    }

    [Fact]
    public void A_reset_that_could_not_write_does_not_select_the_file()
    {
        // The failure case, and the one where selecting a stale file is actively
        // harmful: the write failed, so GamerTool.fac still holds the game's
    // curve, and --preset=GamerTool would apply exactly what we are trying to
    // remove. The scalar values and the gain command still go, so the reset does
        // as much as it can rather than aborting.
        AudioService audio = new();

        IReadOnlyList<string> commands = audio.BuildApplyCommands(AudioPreset.Flat(10), string.Empty, selectPresetFile: false);

        Assert.DoesNotContain(commands, c => c.Contains("--preset=GamerTool", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Contains("--set_band_gain", StringComparison.Ordinal));
    }

    [Fact]
    public void The_reset_writes_the_curve_rather_than_only_selecting_it()
    {
        // Pinned against the source, because the write is the part with no return
        // value to assert on and the whole defect lived in its absence. Without it
        // the fix can look present - the gains command is there - while the engine
        // is still being handed the game's curve off disk.
        string source = Read("app/Services/AudioService.cs");
        string body = BodyAfter(source, "public async System.Threading.Tasks.Task ResetSoundAsync");

        Assert.Contains("FxPresetFile.Write(", body, StringComparison.Ordinal);
        Assert.Contains("BuildApplyCommands(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Run(BuildApplyCommand(flat", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exit_path_writes_the_curve_too()
    {
        // The same defect on the way out. EmergencyReset sent one apply command, so
        // quitting with a game's tune loaded selected that tune on the way out -
        // the one moment where leaving a game's equaliser in place is least wanted
        // and least noticed, because there is nobody left to hear it change back.
        string source = Read("app/Services/EmergencyReset.cs");

        Assert.Contains("FxPresetFile.Write(", source, StringComparison.Ordinal);
        Assert.Contains("BuildApplyCommands(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildApplyCommand(flat, string.Empty)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reset_never_applies_the_loud_guard()
    {
        // The guard is deliberately absent from both reset paths. It is a safety
        // control that holds a loud game down, and a guard that fought an attempt
        // to put the system back would be indefensible - the panic key is exactly
        // the moment somebody needs the gain back.
        string reset = BodyAfter(Read("app/Services/AudioService.cs"),
            "public async System.Threading.Tasks.Task ResetSoundAsync");

        Assert.DoesNotContain("WithLoudGuard", reset, StringComparison.Ordinal);

        // And the exit path forces the effects on so the flat curve is the thing
        // that lands, then puts the user's own state back.
        string exit = Read("app/Services/EmergencyReset.cs");
        Assert.Contains("audio.EffectsEnabled = true;", exit, StringComparison.Ordinal);
        Assert.Contains("audio.EffectsEnabled = wasBypassed;", exit, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static string BodyAfter(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);

        int end = source.IndexOf("\n    public ", at + signature.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.Length;
        }

        return source.Substring(at, end - at);
    }

    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}