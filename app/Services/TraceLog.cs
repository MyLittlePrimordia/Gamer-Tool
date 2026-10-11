using System;
using System.Text;

namespace GamerTool.Services;

/// <summary>
/// The app's existing trace call sites, now going through <see cref="AppLog"/> so
/// there is one log file rather than two that disagree. Kept as a type so that
/// nothing scattered through the app has to change.
/// </summary>
public static class TraceLog
{
    public static void Write(string message) => AppLog.Info(message);

    public static void Write(string tag, Exception ex) => AppLog.Error(tag, ex);
}
