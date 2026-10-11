using System;

namespace GamerTool.Services;

/// <summary>
/// What the app is prepared to say about FxSound's health, and the words for it.
/// <para>
/// The failure this exists for is the ordinary one: a user's equaliser does
/// nothing, the Audio tab looks entirely normal, and nothing anywhere says the
/// engine is not running. "Why isn't my EQ doing anything?" is the most likely
/// support question this app can generate, and there are four answers to it -
/// not installed, not running, snapshot stale, powered off - which before this
/// were all indistinguishable from working.
/// </para>
/// <para>
/// A pure decision over five values with the wording attached, because the same
/// four answers have to be reachable from the Audio tab and from the tray
/// tooltip and those two cannot be allowed to disagree. Only one of them decides
/// what is said; the other decides where it is drawn.
/// </para>
/// <para>
/// No FileSystemWatcher, and deliberately so. The status file is already behind a
/// three second cache and is only read when something wants engine state, so a
/// watcher would add a disposable handle, a debounce timer and a fallback poll to
/// replace a cache that works.
/// </para>
/// </summary>
public sealed class FxHealth
{
    /// <summary>How the engine is doing. Five answers, in the order they are checked.</summary>
    public enum State
    {
        /// <summary>FxSound is not on this machine.</summary>
        Missing,

        /// <summary>Installed, but no engine process is running.</summary>
        NotRunning,

        /// <summary>
        /// The app cannot currently see what the engine is doing - either the
        /// status file is missing or it is too old to describe what is playing.
        /// </summary>
        Stale,

        /// <summary>
        /// Running, but the user has told it not to process anything. Not the same
        /// as not running, and saying "off" would send them looking for a crash
        /// they do not have.
        /// </summary>
        PoweredOff,

        /// <summary>Everything the app needs is present and current.</summary>
        Ok,
    }

    /// <summary>Which of the five it is.</summary>
    public State HowItIs { get; private init; }

    /// <summary>
    /// What the engine said about its own EQ, which is three answers and not two.
    /// <para>
    /// <see cref="Off"/> is only ever produced when the engine positively reports
    /// being off. <see cref="Unknown"/> is "it did not say", and it is a separate
    /// answer on purpose: the field is a plain bool defaulting to false, and a
    /// status file without a <c>power</c> key reads as false unless something is
    /// done about it. That conflation put "EQ switched off" in front of users whose
    /// EQ was on, and the status line had no way to be wrong about it more loudly
    /// than the file was.
    /// </para>
    /// </summary>
    public enum PowerState
    {
        /// <summary>The file did not carry the field. Nothing is being claimed.</summary>
        Unknown,

        /// <summary>The engine reports itself powered.</summary>
        On,

        /// <summary>The engine reports itself not processing.</summary>
        Off,
    }

    /// <summary>Short line for the status area. UPPER CASE, like every other one.</summary>
    public string Caption { get; private init; } = string.Empty;

    /// <summary>What to do about it, when there is something to do.</summary>
    public string Detail { get; private init; } = string.Empty;

    /// <summary>True only for <see cref="State.Ok"/>.</summary>
    public bool IsHealthy => HowItIs == State.Ok;

    /// <summary>
    /// Whether this is something the user has to go and fix.
    /// <para>
    /// Deliberately not the same question as <see cref="IsHealthy"/>.
    /// <see cref="State.PoweredOff"/> is not healthy - the EQ genuinely is not doing
    /// anything - but nothing is wrong either. The user switched it off on purpose,
    /// and applying any preset switches it back on by itself, so there is no
    /// problem to report and nobody to send looking for one.
    /// </para>
    /// <para>
    /// Drawing that state in the same alarm colour as an engine that is not
    /// answering is what made a normal resting state read as a broken install, and
    /// it was reported as confusing exactly that: a new user reads "bypassed" as
    /// "FxSound is not working". The two answers have to be told apart visually or
    /// the distinction this type exists to draw is lost on the screen.
    /// </para>
    /// </summary>
    public bool NeedsAttention => HowItIs is State.Missing or State.NotRunning or State.Stale;

    /// <summary>Whether to offer the install button.</summary>
    public bool OffersInstall { get; private init; }

    /// <summary>Whether to offer the start-engine button.</summary>
    public bool OffersStart { get; private init; }

    /// <summary>
    /// Answers the question from the five facts that can be read cheaply.
    /// </summary>
    /// <param name="installed">Whether the executable exists where the profile says.</param>
    /// <param name="running">Whether an engine process exists.</param>
    /// <param name="snapshotPresent">Whether a status file was found at all.</param>
    /// <param name="snapshotFresh">Whether it is recent enough to describe what is playing.</param>
    /// <param name="power">What the engine reported about itself. Anything other than
    /// <see cref="PowerState.Off"/> leaves the EQ out of the answer entirely.</param>
    /// <remarks>
    /// The order is the whole of it, and each position exists because of a specific
    /// wrong answer that putting it elsewhere produces:
    /// <para>
    /// Installed first, because a snapshot read before an uninstall would otherwise
    /// report NotRunning on a machine with no FxSound at all.
    /// </para>
    /// <para>
    /// Running second, for the same reason one step down - a stale file names
    /// whatever engine last wrote it.
    /// </para>
    /// <para>
    /// Freshness third, before <paramref name="powerOn"/>. A stale file can well say
    /// power: true, and reading it as live is how the app ends up reporting a curve
    /// as applied when it was applied an hour ago. Stale also covers "no snapshot",
    /// because a crash that stopped the engine writing the file is the worse case
    /// and must not be the one that needs no attention.
    /// </para>
    /// <para>
    /// Power last, and only once everything else is known good.
    /// <para>
    /// And only when the engine actually said. A status file that does not carry the
    /// field reaches here as <see cref="PowerState.Unknown"/> and lands in
    /// <see cref="State.Ok"/>, because the app cannot see a fault here and must not
    /// invent one - the same rule the rest of this type follows, and the reason it
    /// took a separate enum to get right.
    /// </para>
    /// </remarks>
    public static FxHealth Decide(
        bool installed,
        bool running,
        bool snapshotPresent,
        bool snapshotFresh,
        PowerState power)
    {
        if (!installed)
        {
            return new FxHealth
            {
                HowItIs = State.Missing,
                Caption = "NOT INSTALLED",
                Detail = "Not installed yet.",
                OffersInstall = true,
            };
        }

        if (!running)
        {
            return new FxHealth
            {
                HowItIs = State.NotRunning,
                Caption = "NOT RUNNING",
                Detail = "Not running.",
                OffersStart = true,
            };
        }

        if (!snapshotPresent || !snapshotFresh)
        {
            return new FxHealth
            {
                HowItIs = State.Stale,
                Caption = "NO RESPONSE",
                Detail = "Not reporting settings.",
            };
        }

        if (power == PowerState.Off)
        {
            // Wording rewritten twice now, both times for length.
            //
            // It used to say the sliders "have nothing to act on", which is true and
            // reads as a fault: it tells the user their controls are dead without
            // saying that the next thing they do will fix it by itself. "Bypassed"
            // is also engine jargon, and it was the word in the caption a new user
            // was most likely to read as "this is broken".
            //
            // The replacement then carried two sentences: that the settings are not being
            // heard, and that applying a preset switches it back on. The first is
            // already the caption immediately beside this, said twice, and the pair
            // ran to a hundred and twenty characters on a status line somebody is
            // trying to read at a glance.
            //
            // What earns its place is the half that is not on screen anywhere else:
            // that one click reverses it, and that the click is applying a preset.
            // That is what The_switched_off_message_says_what_to_do_about_it holds
            // this to, so the wording keeps both of the phrases it checks for.
            return new FxHealth
            {
                HowItIs = State.PoweredOff,
                Caption = "EQ SWITCHED OFF",
                Detail = "Apply a preset and it switches back on by itself.",
            };
        }

        return new FxHealth
        {
            HowItIs = State.Ok,
            Caption = "READY",
        };
    }
}