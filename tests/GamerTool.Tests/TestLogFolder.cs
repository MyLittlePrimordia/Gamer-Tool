using System;
using System.IO;
using System.Runtime.CompilerServices;
using GamerTool.Services;

namespace GamerTool.Tests;

/// <summary>
/// Keeps the test suite out of the real log.
/// <para>
/// A fair number of tests exist precisely to drive the paths that log a failure:
/// a settings file that will not parse, one that cannot be read, engine output
/// that is not JSON, a signature that is not FxSound's. Those all reach
/// <see cref="AppLog"/>, which writes to one fixed folder - the same one the
/// Settings tab's "copy log" button reads.
/// </para>
/// <para>
/// So a test run fills a user's diagnostic log with failures that never happened
/// on their machine, and the log is the one artefact a user is asked to paste
/// into a bug report. It has to go somewhere else.
/// </para>
/// <para>
/// A module initializer rather than a fixture, because this has to be in force
/// before any test runs and a collection fixture would only cover its own
/// collection.
/// </para>
/// </summary>
internal static class TestLogFolder
{
    internal static string Folder { get; } = Path.Combine(
        Path.GetTempPath(),
        "GamerToolTests",
        "logs-" + Guid.NewGuid().ToString("N")[..8]);

    [ModuleInitializer]
    internal static void Redirect()
    {
        AppLog.RedirectTo(Folder);

        try
        {
            Directory.CreateDirectory(Folder);
        }
        catch (Exception)
        {
            // If it cannot be made, AppLog swallows its own write failures, so the
            // only cost is that the log lines go nowhere at all - which is
            // exactly what was wanted anyway.
        }
    }
}
