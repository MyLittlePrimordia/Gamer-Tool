using System;
using System.Collections.Generic;
using System.Linq;

namespace GamerTool.Services;

/// <summary>
/// Why a display could not be driven, in words that name the actual cause.
/// <para>
/// This exists because "this monitor doesn't support hardware brightness" is
/// usually false. A display can be perfectly capable and still be unreachable:
/// on the machine this was written for, a 240Hz OLED that had answered a VCP 0x10
/// read minutes earlier was being told it did not support brightness, because the
/// graphics driver was not providing a DDC/CI link to it. Blaming the panel sends
/// people looking for a monitor fault that is not there.
/// </para>
/// <para>
/// Short on purpose. This is a line under a monitor's name, and the technical
/// half belongs in Copy diagnostics, which is where it was always meant to be read.
/// </para>
/// </summary>
public static class BacklightStatus
{
    /// <summary>What the row says, for a display that cannot be driven.</summary>
    public static string For(MonitorProbe monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        if (monitor.CanControlBacklight)
        {
            return "Monitor's own backlight, over DDC/CI. Your brightness slider above bends the picture instead.";
        }

        if (monitor.BlockedReason is not null)
        {
            return BlockedCopy(monitor.BlockedReason);
        }

        return monitor.Outcome switch
        {
            // The monitor answered and said no. That is the only case where the
            // monitor is actually the thing that declined.
            BusOutcome.Refused => "This display didn't respond to brightness requests.",

            // Nothing was asked, so nothing can be concluded and saying the display
            // does not support it would be a guess.
            BusOutcome.Busy => "Not checked yet, the display was busy.",
            BusOutcome.TimedOut => "Didn't reply in time, so it was left alone.",

            // The driver listed a physical monitor and gave it no DDC/CI handle.
            // The panel is not at fault and neither is the cable; the link between
            // the driver and the display is missing.
            BusOutcome.NoDdcPathway => "No DDC/CI link from the graphics driver to this display.",

            // A stub target means the driver has not set the display up at all,
            // which is a stronger and more accurate statement than a missing handle
            // and points at the same place.
            _ when monitor.Target.Health == TargetHealth.Stub =>
                "The graphics driver has not set this display up.",

            _ => "Unavailable on this display."
        };
    }

    /// <summary>
    /// Copy for a display the app refused to probe, which is a different decision
    /// from a display that failed.
    /// </summary>
    private static string BlockedCopy(string blockedReason) =>
        blockedReason.StartsWith("on your exclusion", StringComparison.Ordinal)
            ? "Left alone after repeated failures. Switch hardware brightness off and on to try again."
            : "Left alone: its details did not look right.";

    // This class used to also carry a ShouldRetireMachine predicate answering the
    // whole retirement question in one line. Nothing in the app called it:
    // BacklightAvailability.Record decides as it goes, because the count has to
    // carry between rounds and across a restart and cannot be recomputed from a
    // list of monitors on demand.
    //
    // Three tests called the unused predicate rather than the code that runs, so
    // the policy they were written to protect - one working display among several
    // keeps the feature, a machine with no displays is never retired - was being
    // asserted against a second parallel implementation. Both cases are covered in
    // BacklightAvailabilityTests against Record itself.
}
