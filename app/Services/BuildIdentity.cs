using System;
using System.Reflection;

namespace GamerTool.Services;

/// <summary>
/// Which build is running, as opposed to which version.
/// <para>
/// The version is 1.0.0 and has been since the first release, and the release
/// itself is a rolling "latest" whose asset is replaced on every push. So the
/// version number cannot answer the first question of any bug report - which
/// build is this - and neither the log nor the diagnostics text carried anything
/// else.
/// </para>
/// <para>
/// The value comes from an <see cref="AssemblyMetadataAttribute"/> written by the
/// build: CI sets the short commit and run number, and a local build says
/// <c>dev</c>. Metadata rather than a generated source file because there is one
/// string and the generator would be more moving parts than the thing it holds.
/// </para>
/// </summary>
internal static class BuildIdentity
{
    /// <summary>Short commit and run number, or <c>dev</c> for a local build.</summary>
    public static string Id { get; } = Read();

    private static string Read()
    {
        try
        {
            foreach (AssemblyMetadataAttribute attribute in
                typeof(BuildIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(attribute.Key, "BuildId", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(attribute.Value))
                {
                    return attribute.Value;
                }
            }
        }
        catch (Exception)
        {
            // A missing attribute is not worth failing a start over, and the
            // fallback is the same answer a local build gives.
        }

        return "dev";
    }
}
