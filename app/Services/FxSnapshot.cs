namespace GamerTool.Services;

/// <summary>
/// What the engine looked like before this app touched it.
/// <para>
/// Taken once, before the first thing this app does to FxSound, and handed back on
/// the way out. The reason it exists is that a great many people run FxSound as
/// their own player with a preset of their own, and installed this app alongside
/// it. The exit path used to push a flat curve and run <c>--power=0</c> on every
/// one of them, which is an app taking over something it was only borrowing: they
/// came back to a muted machine and no equaliser.
/// </para>
/// <para>
/// A record rather than a class so a snapshot is a value. It is compared, passed
/// to a judgement, and thrown away on one exit path; identity would mean nothing
/// and mutable fields would mean a snapshot could be edited after it was taken,
/// which is the one thing it must not be.
/// </para>
/// </summary>
/// <param name="Power">
/// Whether the engine was powered on. False is knowledge and true is knowledge,
/// but a caller that could not read it must produce no snapshot at all - which is
/// why this is a plain bool rather than a nullable one.
/// </param>
/// <param name="Preset">The preset the engine had selected, or empty for none.</param>
/// <param name="Output">The output device the engine was on. Recorded but never required.</param>
public sealed record FxSnapshot(bool Power, string Preset, string Output);