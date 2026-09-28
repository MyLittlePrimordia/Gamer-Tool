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
    /// Names that mean "this is a monitor, not a speaker". Audio routed here is
    /// silent, because the endpoint has no speakers behind it.
    /// </summary>
    private static readonly string[] DisplayOutputHints =
    {
        "AG276", "AMD High Definition", "Display Audio", "Monitor",
        "HDMI", "DisplayPort", "DP ", "TV", "Intel Display"
    };

    /// <summary>
    /// True for HDMI or DisplayPort audio on a monitor.
    /// </summary>
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

        return false;
    }

    /// <param name="savedOutputDeviceId">
    /// What the user has already chosen in settings. Anything non-empty means
    /// they have an opinion and it is not ours to override.
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
        // The user picked an output. Leave it be, even if it looks wrong: they may
        // have picked it on purpose, for a capture device or a second setup.
        if (!string.IsNullOrEmpty(savedOutputDeviceId))
        {
            return Decision.Leave;
        }

        // Nothing is pointing at a monitor, so there is nothing to fix.
        if (!LooksLikeDisplayOutput(selectedOutput) || !LooksLikeDisplayOutput(defaultOutput))
        {
            return Decision.Leave;
        }

        List<AudioDeviceInfo> real = endpoints
            .Where(device => !LooksLikeDisplayOutput(device.Name))
            .Where(device => engineKnownNames.Any(k => k.Contains(device.Name, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

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

        AudioDeviceInfo target = real[0];
        string key = engineInstalled ? target.Name : target.Id;
        return new Decision(Action.Switch, key, target.Name, null);
    }
}
