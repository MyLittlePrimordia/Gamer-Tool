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
    private const int DisplayAudio = OutputRouting.DisplayAudioFormFactor;
    private const int SpeakerForm = 1;
    private const int UnknownForm = 10;

    private static AudioDeviceInfo Device(string id, string name) => new() { Id = id, Name = name };

    /// <summary>An endpoint whose form factor was read.</summary>
    private static AudioDeviceInfo Typed(string id, string name, int formFactor) =>
        new() { Id = id, Name = name, FormFactor = formFactor };

    /// <summary>
    /// A monitor's audio over HDMI. The form factor is what identifies it; the name
    /// is deliberately unremarkable so a test that passed was passing on the form
    /// factor rather than on the word "Monitor".
    /// </summary>
    private static readonly AudioDeviceInfo Monitor = Typed("mon-1", "Audio Output", DisplayAudio);

    private static readonly AudioDeviceInfo Speakers = Typed("spk-1", "Realtek High Definition Audio", SpeakerForm);

    private static readonly AudioDeviceInfo Headphones = Typed("hp-1", "USB Headset", 3);

    [Fact]
    public void TheFormFactorIsWhatIdentifiesADisplayEndpoint()
    {
        // The whole point of the change. This endpoint says nothing about being a
        // monitor in its name, and asking Windows is what settles it.
        Assert.True(OutputRouting.IsDisplayAudio(Typed("d1", "Audio Output", DisplayAudio)));
    }

    [Fact]
    public void ARealSpeakerIsNotDisplayAudioEvenWhenItsNameSaysMonitor()
    {
        // The bug this replaces. "Studio Monitor" is a speaker, it contains the word
        // the old filter looked for, and being classified as display audio removed it
        // from the candidate list - so the app reported no speakers on a machine that
        // has them.
        AudioDeviceInfo studioMonitor = Typed("sm-1", "Studio Monitor Speakers", SpeakerForm);

        Assert.False(OutputRouting.IsDisplayAudio(studioMonitor));
        Assert.Equal(
            OutputRouting.Action.Switch,
            OutputRouting.Decide(
                savedOutputDeviceId: string.Empty,
                selectedOutput: Monitor.Name,
                defaultOutput: Monitor.Name,
                endpoints: new[] { Monitor, studioMonitor },
                engineKnownNames: new[] { studioMonitor.Name },
                engineInstalled: true).Action);
    }

    [Fact]
    public void AnOpticalOutputOnATelevisionIsNotASetOfSpeakers()
    {
        // Which is why "TV" survived as a whole word rather than being deleted. It is
        // a real case, and getting it wrong would offer a TV's optical out as the
        // thing to route game audio to.
        Assert.True(OutputRouting.LooksLikeDisplayOutput("TV"));
        Assert.True(OutputRouting.LooksLikeDisplayOutput("LG TV"));
        Assert.True(OutputRouting.LooksLikeDisplayOutput("TV Optical"));
    }

    [Fact]
    public void OneMonitorModelIsNoLongerAHardcodedRule()
    {
        // "AG276" was a specific monitor - the machine this was written on - shipped as
        // a general filter. It did nothing on every other monitor and can only ever
        // have been right about one.
        Assert.False(OutputRouting.LooksLikeDisplayOutput("AG276QG Monitor"));
    }

    [Fact]
    public void AMonitorWithAHeadphoneJackIsARealOutput()
    {
        // A display with a 3.5 mm jack reports Speakers, and it is genuinely one. The
        // old "Monitor" hint could not tell that from a monitor with no jack at all.
        AudioDeviceInfo monitorSpeakers = Typed("ms-1", "BenQ Monitor Speakers", SpeakerForm);

        Assert.False(OutputRouting.IsDisplayAudio(monitorSpeakers));
    }

    [Fact]
    public void AnUnknownFormFactorFallsBackToTheName()
    {
        // A driver that answers "unknown" has told us nothing, so the name is all
        // there is. This is the path that keeps working on hardware whose property
        // does not read.
        AudioDeviceInfo unknown = new() { Id = "u-1", Name = "DisplayPort Output", FormFactor = 10 };

        Assert.True(OutputRouting.IsDisplayAudio(unknown));
    }

    [Fact]
    public void AFormFactorThatCouldNotBeReadFallsBackToTheName()
    {
        // Different from UnknownFormFactor and kept different: -1 means the property
        // was never read, which is a failure worth logging, where 10 is the driver
        // answering that it does not know. Both fall back, but only one is our bug.
        Assert.True(OutputRouting.IsDisplayAudio(Device("r-1", "HDMI Output")));
        Assert.False(OutputRouting.IsDisplayAudio(Device("r-2", "Realtek High Definition Audio")));
    }

    [Fact]
    public void AnOutputTheEnumerationDidNotReturnIsNotGuessedAt()
    {
        // The engine can name an output the endpoint walk did not return. Treating
        // that name as display audio would let a guess start the whole repair, which
        // is the one outcome here that can send audio somewhere the user did not ask.
        OutputRouting.Decision decision = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: "Some Output The Enumeration Missed",
            defaultOutput: "Another One We Never Saw",
            endpoints: new[] { Monitor, Speakers },
            engineKnownNames: new[] { Speakers.Name },
            engineInstalled: true);

        Assert.Equal(OutputRouting.Action.None, decision.Action);
    }

    [Fact]
    public void MonitorNamesAreDetectedAsDisplays()
    {
        // A display endpoint is silent no matter what is plugged into it. Names only,
        // because that is the fallback and it still has to work on its own.
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
            selectedOutput: Monitor.Name,
            defaultOutput: Monitor.Name,
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
            selectedOutput: Monitor.Name,
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
            selectedOutput: Monitor.Name,
            defaultOutput: Monitor.Name,
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
            selectedOutput: Monitor.Name,
            defaultOutput: Monitor.Name,
            endpoints: new[] { Monitor, Speakers },
            engineKnownNames: new[] { "Realtek High Definition Audio" },
            engineInstalled: true);
        Assert.Equal("Realtek High Definition Audio", withEngine.TargetId);

        // FxSound absent: it is keyed by endpoint id instead. Writing the name
        // here would save a setting the picker can never match, and the box would
        // silently fall back to "System default".
        OutputRouting.Decision withoutEngine = OutputRouting.Decide(
            savedOutputDeviceId: string.Empty,
            selectedOutput: Monitor.Name,
            defaultOutput: Monitor.Name,
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
            selectedOutput: Monitor.Name,
            defaultOutput: Monitor.Name,
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
            selectedOutput: Monitor.Name,
            defaultOutput: Monitor.Name,
            endpoints: new[] { Monitor },
            engineKnownNames: new[] { Monitor.Name },
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
            selectedOutput: Monitor.Name,
            defaultOutput: Monitor.Name,
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
            selectedOutput: Monitor.Name,
            defaultOutput: Monitor.Name,
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
                string.Empty, Monitor.Name, Monitor.Name,
                new[] { Monitor }, new[] { Monitor.Name }, true),
            OutputRouting.Decide(
                string.Empty, Monitor.Name, Monitor.Name,
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
            Monitor.Name,
            Monitor.Name,
            endpoints,
            known,
            engineInstalled: false);

        Assert.Equal(OutputRouting.Action.Switch, decision.Action);
        Assert.Contains(endpoints, d => d.Id == decision.TargetId);
    }
}
