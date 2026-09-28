using System.Collections.Generic;
using System.Linq;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Launching with the output pointed at a monitor means every preset sounds
/// muted, so the app tries to route to real speakers. The failure mode that
/// matters is not the freeze it avoids, it is sending game audio to the wrong
/// speakers: the user hears sound somewhere they did not ask for, or in another
/// room, and has no idea the app did it.
/// <para>
/// These pin the decision itself. The device enumeration cannot be tested
/// without hardware, so the rule is the part that has to be right.
/// </para>
/// </summary>
public class OutputRoutingTests
{
    private static AudioDeviceInfo Device(string id, string name) => new() { Id = id, Name = name };

    private static readonly AudioDeviceInfo Monitor = Device("mon-1", "AG276QG Monitor");
    private static readonly AudioDeviceInfo Speakers = Device("spk-1", "Realtek High Definition Audio");
    private static readonly AudioDeviceInfo Headphones = Device("hp-1", "USB Headset");

    [Fact]
    public void MonitorNamesAreDetectedAsDisplays()
    {
        // A display endpoint is silent no matter what is plugged into it.
        Assert.True(OutputRouting.LooksLikeDisplayOutput("AG276QG (NVIDIA) Monitor"));
        Assert.True(OutputRouting.LooksLikeDisplayOutput("LG ULTRAWIDE HDMI"));
        Assert.True(OutputRouting.LooksLikeDisplayOutput("Display Audio"));
        Assert.True(OutputRouting.LooksLikeDisplayOutput("AMD High Definition Audio Device"));
    }

    [Fact]
    public void SpeakerNamesAreNotMistakenForDisplays()
    {
        // The dangerous direction is a real speaker being classified as a monitor,
        // because that silently removes it from the candidate list.
        Assert.False(OutputRouting.LooksLikeDisplayOutput("Realtek High Definition Audio"));
        Assert.False(OutputRouting.LooksLikeDisplayOutput("USB Headset"));
        Assert.False(OutputRouting.LooksLikeDisplayOutput("Speakers (Realtek High Definition Audio)"));
    }

    [Fact]
    public void EmptyNameIsNotADisplay()
    {
        Assert.False(OutputRouting.LooksLikeDisplayOutput(null));
        Assert.False(OutputRouting.LooksLikeDisplayOutput(string.Empty));
        Assert.False(OutputRouting.LooksLikeDisplayOutput("   "));
    }

    [Fact]
    public void UserChoiceIsNeverOverridden()
    {
        // Even when the current output is a monitor, an explicit choice stands.
        // The user may have picked a capture device on purpose.
        OutputRouting.Decision decision = OutputRouting.Decide(
            savedOutputDeviceId: "Realtek High Definition Audio",
            selectedOutput: "AG276QG Monitor",
            defaultOutput: "AG276QG Monitor",
            endpoints: new[] { Monitor, Speakers },
            engineKnownNames: new[] { "Realtek High Definition Audio" },
            engineInstalled: true);

        Assert.Equal(OutputRouting.Action.None, decision.Action);
    }

    [Fact]
    public void NothingHappensWhenTheOutputIsNotAMonitor()
    {
        // The common case: speakers are already the output, leave it alone.
        OutputRouting.Decision decision = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: "Realtek High Definition Audio",
            defaultOutput: "Realtek High Definition Audio",
            endpoints: new[] { Monitor, Speakers },
            engineKnownNames: new[] { "Realtek High Definition Audio" },
            engineInstalled: true);

        Assert.Equal(OutputRouting.Action.None, decision.Action);
    }

    [Fact]
    public void NothingHappensWhenOnlyTheCurrentOutputIsAMonitor()
    {
        // The engine is on a monitor but Windows default is real speakers, so the
        // default is already correct. Repointing would be a change nobody asked for.
        OutputRouting.Decision decision = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: "AG276QG Monitor",
            defaultOutput: "Realtek High Definition Audio",
            endpoints: new[] { Monitor, Speakers },
            engineKnownNames: new[] { "Realtek High Definition Audio" },
            engineInstalled: true);

        Assert.Equal(OutputRouting.Action.None, decision.Action);
    }

    [Fact]
    public void SingleRealSpeakerIsSelected()
    {
        OutputRouting.Decision decision = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: "AG276QG Monitor",
            defaultOutput: "AG276QG Monitor",
            endpoints: new[] { Monitor, Speakers },
            engineKnownNames: new[] { "Realtek High Definition Audio" },
            engineInstalled: true);

        Assert.Equal(OutputRouting.Action.Switch, decision.Action);
        Assert.Equal("Realtek High Definition Audio", decision.TargetName);
    }

    [Fact]
    public void InstalledEngineKeysTheSettingByNameAndAbsentEngineById()
    {
        // FxSound present: the settings box is keyed by endpoint name.
        OutputRouting.Decision withEngine = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: "AG276QG Monitor",
            defaultOutput: "AG276QG Monitor",
            endpoints: new[] { Monitor, Speakers },
            engineKnownNames: new[] { "Realtek High Definition Audio" },
            engineInstalled: true);
        Assert.Equal("Realtek High Definition Audio", withEngine.TargetId);

        // FxSound absent: it is keyed by endpoint id instead. Writing the name
        // here would save a setting the picker can never match, and the box would
        // silently fall back to "System default".
        OutputRouting.Decision withoutEngine = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: "AG276QG Monitor",
            defaultOutput: "AG276QG Monitor",
            endpoints: new[] { Monitor, Speakers },
            engineKnownNames: new[] { "Realtek High Definition Audio" },
            engineInstalled: false);
        Assert.Equal("spk-1", withoutEngine.TargetId);
    }

    [Fact]
    public void SeveralCandidatesWarnInsteadOfGuessing()
    {
        // Choosing here could send game audio to a headset the user is not wearing.
        OutputRouting.Decision decision = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: "AG276QG Monitor",
            defaultOutput: "AG276QG Monitor",
            endpoints: new[] { Monitor, Speakers, Headphones },
            engineKnownNames: new[] { "Realtek High Definition Audio", "USB Headset" },
            engineInstalled: true);

        Assert.Equal(OutputRouting.Action.Warn, decision.Action);
        Assert.Equal("Pick your speakers in Settings", decision.Message);
        Assert.Null(decision.TargetId);
    }

    [Fact]
    public void NoSpeakersAtAllWarnsWithADifferentMessage()
    {
        // Distinguished from the ambiguous case on purpose: "no speakers found"
        // tells the user to plug something in, where "pick your speakers" tells
        // them to choose. Collapsing them into one message loses that.
        OutputRouting.Decision decision = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: "AG276QG Monitor",
            defaultOutput: "AG276QG Monitor",
            endpoints: new[] { Monitor },
            engineKnownNames: new[] { "AG276QG Monitor" },
            engineInstalled: true);

        Assert.Equal(OutputRouting.Action.Warn, decision.Action);
        Assert.Equal("No speakers found, pick an output", decision.Message);
    }

    [Fact]
    public void EndpointsTheEngineDoesNotKnowAreNotCandidates()
    {
        // A Bluetooth endpoint can be visible to the system but absent from the
        // engine's list. Selecting it would store a setting the engine rejects,
        // which is silence with no explanation.
        OutputRouting.Decision decision = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: "AG276QG Monitor",
            defaultOutput: "AG276QG Monitor",
            endpoints: new[] { Monitor, Speakers, Headphones },
            engineKnownNames: new[] { "Realtek High Definition Audio" },
            engineInstalled: true);

        Assert.Equal(OutputRouting.Action.Switch, decision.Action);
        Assert.Equal("Realtek High Definition Audio", decision.TargetName);
    }

    [Fact]
    public void SameNameOnTwoEndpointsCountsAsOneSpeaker()
    {
        // Windows reports the same physical device once per endpoint, so a
        // two-output interface looks like two candidates and would trigger the
        // ambiguous warning every launch.
        OutputRouting.Decision decision = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: "AG276QG Monitor",
            defaultOutput: "AG276QG Monitor",
            endpoints: new[]
            {
                Monitor,
                Device("spk-a", "Realtek High Definition Audio"),
                Device("spk-b", "realtek high definition audio"),
            },
            engineKnownNames: new[] { "Realtek High Definition Audio" },
            engineInstalled: true);

        Assert.Equal(OutputRouting.Action.Switch, decision.Action);
        Assert.Equal("Realtek High Definition Audio", decision.TargetName);
    }

    [Fact]
    public void EveryWarningCarriesAMessage()
    {
        // A blank toast is worse than no toast: the user is told something is
        // wrong but not what, and the only visible symptom is no sound.
        foreach (OutputRouting.Decision decision in new[]
        {
            OutputRouting.Decide(
                string.Empty, "AG276QG Monitor", "AG276QG Monitor",
                new[] { Monitor }, new[] { "AG276QG Monitor" }, true),
            OutputRouting.Decide(
                string.Empty, "AG276QG Monitor", "AG276QG Monitor",
                new[] { Monitor, Speakers, Headphones },
                new[] { "Realtek High Definition Audio", "USB Headset" }, true),
        })
        {
            Assert.Equal(OutputRouting.Action.Warn, decision.Action);
            Assert.False(string.IsNullOrWhiteSpace(decision.Message));
            Assert.Null(decision.TargetId);
            Assert.Null(decision.TargetName);
        }
    }

    [Fact]
    public void ASwitchAlwaysCarriesAKeyThatMatchesAKnownName()
    {
        // The stored key has to be one the engine recognises, otherwise the
        // setting is written, looks applied, and routes nothing. Exactly one
        // candidate here, because two would correctly warn instead.
        List<AudioDeviceInfo> endpoints = new() { Monitor, Speakers, Headphones };
        List<string> known = new() { "USB Headset" };

        OutputRouting.Decision decision = OutputRouting.Decide(
            string.Empty,
            "AG276QG Monitor",
            "AG276QG Monitor",
            endpoints,
            known,
            engineInstalled: false);

        Assert.Equal(OutputRouting.Action.Switch, decision.Action);
        Assert.Contains(endpoints, d => d.Id == decision.TargetId);
    }
}
