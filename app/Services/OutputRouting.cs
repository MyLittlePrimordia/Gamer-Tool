using System;
using System.Collections.Generic;
using System.Linq;

namespace GamerTool.Services;

/// <summary>
/// Works out what should happen to the audio output at launch, as a pure
/// decision over values that have already been read.
/// <para>
/// This is deliberately separate from the code that reads the devices. Routing
/// game audio to the wrong speakers is a bug the user notices immediately and
/// cannot work around, and the rule that decides it is a stack of six branches
/// over a list that has to be filtered, de-duplicated and cross-checked against
/// what the engine will actually accept. Reading the devices needs a live
/// machine and cannot be tested; deciding what to do with the result can, and
/// that is the part worth pinning down.
/// </para>
/// </summary>
public static class OutputRouting
{
    /// <summary>What to do about the output device. Exactly one field is meaningful.</summary>
    public enum Action
    {
        /// <summary>Leave the output alone.</summary>
        None,

        /// <summary>Switch to <see cref="OutputRouting.Decision.Target"/>.</summary>
        Switch,

        /// <summary>Tell the user <see cref="OutputRouting.Decision.Message"/>.</summary>
        Warn,
    }

    public sealed record Decision(Action Action, string? TargetId, string? TargetName, string? Message)
    {
        public static readonly Decision Leave = new(Action.None, null, null, null);
    }

    /// <summary>
    /// EndpointFormFactor.DigitalAudioDisplayDevice, from
    /// <c>PKEY_AudioEndpoint_FormFactor</c>. Audio routed here is silent, because
    /// the endpoint has no speakers behind it.
    /// <para>
    /// This is the answer Windows already gives, and the only value in this class
    /// that is not a guess about a name. Verified against mmdeviceapi.h: the enum is
    /// RemoteNetworkDevice 0, Speakers 1, LineLevel 2, Headphones 3, Microphone 4,
    /// Headset 5, Handset 6, UnknownDigitalPassthrough 7, SPDIF 8,
    /// DigitalAudioDisplayDevice 9, UnknownFormFactor 10. The name was "HDMI" before
    /// Windows 7.
    /// </para>
    /// </summary>
    public const int DisplayAudioFormFactor = 9;

    /// <summary>
    /// EndpointFormFactor.UnknownFormFactor. A driver that has answered "I do not
    /// know", which is a different claim from a property that could not be read but
    /// is equally useless, so both fall back to the name.
    /// </summary>
    public const int UnknownFormFactor = 10;

    /// <summary>
    /// Names that mean "this is a monitor, not a speaker".
    /// <para>
    /// A fallback for endpoints with no form factor, and only for endpoints with no
    /// form factor - see <see cref="IsDisplayAudio"/>.
    /// </para>
    /// <para>
    /// Three entries were removed from here, and each removal is a bug fix rather
    /// than a tidying:
    /// </para>
    /// <para>
    /// "AG276" was one monitor model, the machine this was written on. It was never
    /// a general rule; it was one person's hardware shipped as a filter, and on every
    /// other monitor the hint did nothing at all.
    /// </para>
    /// <para>
    /// "Monitor" matched "Studio Monitor", which is a speaker, and a real one. The
    /// consequence is the bad direction: a genuine speaker is classed as display
    /// audio, silently dropped from the candidate list, and the app then reports no
    /// speakers on a machine that has them.
    /// </para>
    /// <para>
    /// "TV" matched any name containing those two letters anywhere, including
    /// "TV Optical" and worse. Kept as a whole-word match below instead of deleted,
    /// because an optical output really is not a set of speakers and a monitor with a
    /// 3.5 mm jack is.
    /// </para>
    /// </summary>
    private static readonly string[] DisplayOutputHints =
    {
        "AMD High Definition", "Display Audio", "HDMI", "DisplayPort", "DP ", "Intel Display"
    };

    /// <summary>
    /// Names that are display audio only when the word stands on its own.
    /// <para>
    /// "TV" is the one that needs this. As a fragment it matched half the alphabet;
    /// as a whole word it matches the television it was always meant to catch.
    /// </para>
    /// </summary>
    private static readonly string[] DisplayOutputWholeWords = { "TV" };

    /// <summary>
    /// True for HDMI or DisplayPort audio on a monitor, judged from the name alone.
    /// </summary>
    /// <remarks>
    /// The name path is a guess and is treated as one. Prefer
    /// <see cref="IsDisplayAudio"/>, which asks Windows; this exists for the endpoints
    /// whose form factor could not be read.
    /// </remarks>
    public static bool LooksLikeDisplayOutput(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (string hint in DisplayOutputHints)
        {
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string word in DisplayOutputWholeWords)
        {
            if (ContainsWholeWord(name, word))
            {
                return true;
            }
        }

        return false;
    }

/// <summary>
/// Whether an endpoint is display audio, preferring what Windows says.
/// <para>
/// Two values mean Windows has given no usable answer and the name is consulted
/// instead: -1, where the property could not be read at all, and 10
/// (UnknownFormFactor), where a driver has answered that it does not know. Both
/// fall back because both leave the question exactly where it was, and the name
/// fallback is now narrow enough to be worth trying - it no longer contains "TV"
/// as a fragment or one monitor model as a general rule.
/// </para>
/// <para>
/// Any other value is taken at face value. A driver that reports Speakers or
/// Headset has answered the question, and second-guessing it from the name is
/// what put a Studio Monitor into the display-audio bucket in the first place.
/// </para>
/// <para>
/// Measured on a real machine: a monitor endpoint answered 9 and three sets of
/// speakers answered 1, so the property is doing the work the name list used to do
/// and answering it correctly.
/// </para>
/// </summary>
    public static bool IsDisplayAudio(AudioDeviceInfo? device)
    {
        if (device is null)
        {
            return false;
        }

        return device.FormFactor == UnknownFormFactor || device.FormFactor < 0
            ? LooksLikeDisplayOutput(device.Name)
            : device.FormFactor == DisplayAudioFormFactor;
    }

    /// <summary>
    /// Whether <paramref name="word"/> appears in <paramref name="text"/> with a
    /// non-letter either side of it or at either end.
    /// </summary>
    private static bool ContainsWholeWord(string text, string word)
    {
        int index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            bool startOk = index == 0 || !char.IsLetter(text[index - 1]);
            int after = index + word.Length;
            bool endOk = after >= text.Length || !char.IsLetter(text[after]);

            if (startOk && endOk)
            {
                return true;
            }

            index = text.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <param name="savedOutputDeviceId">
    /// What the user has already chosen in settings. Anything non-empty means
    /// they have an opinion and it is not ours to override - while the endpoint
    /// is still attached. A saved endpoint that has gone is the one case where
    /// the opinion cannot be honoured, and the fallback below applies instead.
    /// </param>
    /// <param name="selectedOutput">The engine's current output.</param>
    /// <param name="defaultOutput">The Windows default playback endpoint.</param>
    /// <param name="endpoints">Every playback endpoint the system reports.</param>
    /// <param name="engineKnownNames">
    /// Output names the engine will accept. Endpoints outside this set cannot be
    /// switched to, because the setting would then name something the engine
    /// rejects and the user would get silence with no explanation.
    /// </param>
    /// <param name="engineInstalled">
    /// True when FxSound is present. The settings box is keyed by endpoint name
    /// when it is and by endpoint id when it is not, so this decides what is
    /// actually storable.
    /// </param>
    public static Decision Decide(
        string savedOutputDeviceId,
        string? selectedOutput,
        string? defaultOutput,
        IReadOnlyList<AudioDeviceInfo> endpoints,
        IReadOnlyList<string> engineKnownNames,
        bool engineInstalled)
    {
        // The user picked an output. Leave it be while it is still there, even if
        // it looks wrong: they may have picked it on purpose, for a capture
        // device or a second setup.
        //
        // "Still there" is matched against both the id and the name, because the
        // value is a name when the engine is installed and an id when it is not
        // (see the key chosen below). Only one of the two can match, and matching
        // the wrong one would make every saved device look absent.
        if (!string.IsNullOrEmpty(savedOutputDeviceId))
        {
            bool savedPresent = endpoints.Any(device =>
                string.Equals(device.Id, savedOutputDeviceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.Name, savedOutputDeviceId, StringComparison.OrdinalIgnoreCase));

            if (savedPresent)
            {
                return Decision.Leave;
            }

            // The saved endpoint has gone - a USB headset unplugged, a dock left
            // behind. Leaving it alone is not neutrality: the engine stays pointed
            // at an endpoint that no longer exists, so every preset is silent with
            // no message. Fall back to something this machine can actually play
            // through.
            return FallBack(endpoints, engineKnownNames, engineInstalled, defaultOutput);
        }

        // Nothing is pointing at a monitor, so there is nothing to fix.
        //
        // Both sides go through the same endpoint list the candidates come from, so
        // the question "is the current output display audio" and the question "is this
        // candidate display audio" cannot be answered two different ways. Matching on
        // names alone meant a monitor the driver did not name recognisably was treated
        // as fine, and the app then said nothing when every preset came out silent.
        if (!IsDisplayAudio(Find(endpoints, selectedOutput))
            || !IsDisplayAudio(Find(endpoints, defaultOutput)))
        {
            return Decision.Leave;
        }

        List<AudioDeviceInfo> real = RealOutputs(endpoints, engineKnownNames);

        if (real.Count == 0)
        {
            return new Decision(Action.Warn, null, null, "No speakers found, pick an output");
        }

        // Several candidates and no way to tell them apart safely. Guessing sends
        // game audio to the wrong room.
        if (real.Count > 1)
        {
            return new Decision(Action.Warn, null, null, "Pick your speakers in Settings");
        }

        return SwitchTo(real[0], engineInstalled);
    }

    /// <summary>
    /// What to do when the saved endpoint is not attached any more.
    /// <para>
    /// The Windows default first, because that is where the machine has already
    /// moved the audio and it is the only choice here that is not a guess. If the
    /// default is not a usable speaker - it is a monitor's silent HDMI output, or
    /// the read failed - a single remaining real output is used.
    /// </para>
    /// <para>
    /// More than one candidate and no default still warns rather than choosing:
    /// picking the wrong one sends game audio to another room, which is the
    /// failure this whole class exists to avoid. The message names the cause,
    /// because "pick your speakers" without "the saved one is gone" leaves the
    /// user wondering why they are being asked again.
    /// </para>
    /// </summary>
    private static Decision FallBack(
        IReadOnlyList<AudioDeviceInfo> endpoints,
        IReadOnlyList<string> engineKnownNames,
        bool engineInstalled,
        string? defaultOutput)
    {
        List<AudioDeviceInfo> real = RealOutputs(endpoints, engineKnownNames);

        if (real.Count == 0)
        {
            return new Decision(Action.Warn, null, null, "Saved output is gone, plug in speakers");
        }

        AudioDeviceInfo? systemDefault = real.FirstOrDefault(device =>
            string.Equals(device.Name, defaultOutput, StringComparison.OrdinalIgnoreCase));

        if (systemDefault is not null)
        {
            return SwitchTo(systemDefault, engineInstalled, "Saved output is gone, now on " + systemDefault.Name);
        }

        if (real.Count == 1)
        {
            return SwitchTo(real[0], engineInstalled, "Saved output is gone, now on " + real[0].Name);
        }

        return new Decision(Action.Warn, null, null, "Saved output is gone, pick an output");
    }

    /// <summary>
    /// Endpoints this machine can actually play through: not a monitor, and named
    /// by the engine, with duplicates collapsed by name.
    /// </summary>
    private static List<AudioDeviceInfo> RealOutputs(
        IReadOnlyList<AudioDeviceInfo> endpoints,
        IReadOnlyList<string> engineKnownNames)
    {
        return endpoints
            .Where(device => !IsDisplayAudio(device))
            .Where(device => engineKnownNames.Any(k => k.Contains(device.Name, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// A switch, keyed the way the settings box stores it: by name when the engine
    /// is installed, by endpoint id when it is not.
    /// </summary>
    private static Decision SwitchTo(AudioDeviceInfo target, bool engineInstalled, string? note = null)
    {
        string key = engineInstalled ? target.Name : target.Id;
        return new Decision(Action.Switch, key, target.Name, note);
    }

    /// <summary>
    /// The endpoint the system is currently using, if it is one we enumerated.
    /// <para>
    /// Null when the name matches nothing, which is the answer that matters: the
    /// engine and Windows can each name an output the enumeration did not return, and
    /// that name has no form factor behind it. Returning null rather than a
    /// name-only guess is what stops this method inventing an endpoint.
    /// </para>
    /// </summary>
    private static AudioDeviceInfo? Find(IReadOnlyList<AudioDeviceInfo> endpoints, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (AudioDeviceInfo device in endpoints)
        {
            if (string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }
}
