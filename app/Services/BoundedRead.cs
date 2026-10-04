using System;
using System.IO;
using System.Text;

namespace GamerTool.Services;

/// <summary>
/// File reads with a ceiling on how much they will take.
/// <para>
/// Every one of these files is small and known: a settings file, an engine status
/// file, a backup, a Steam manifest. All of them are also written by something
/// else - the engine rewrites its status file while this app is reading it, and a
/// Steam library file can be edited by hand - so "small" is a property of the
/// happy path rather than a guarantee. A <c>ReadToEnd</c> with no ceiling turns a
/// file that has grown to something absurd, or a device that has stopped
/// responding mid-read, into an allocation the app cannot recover from.
/// </para>
/// <para>
/// The ceiling is generous by design. It is here to stop a runaway, not to police
/// a legitimate file, and a file over the limit is treated exactly like an
/// unreadable one rather than being parsed as a prefix: half a JSON document is
/// not a JSON document with a shorter value in it.
/// </para>
/// </summary>
internal static class BoundedRead
{
    /// <summary>
    /// Largest settings file accepted, in bytes. A profile carrying every preset,
    /// slot, custom tune and app binding is a few tens of kilobytes; this is an
    /// order of magnitude above that and still small enough to hold in memory
    /// without a thought.
    /// </summary>
    internal const long DefaultCeilingBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Reads the whole file, shared, refusing anything past the ceiling.
    /// <para>
    /// Shared, because two of these are read while the thing that writes them has
    /// them open. <c>File.ReadAllText</c> defaults to <see cref="FileShare.Read"/>
    /// and fails the moment the engine holds its status file for writing, which
    /// is a race the app does not get to choose the timing of.
    /// </para>
    /// </summary>
    /// <exception cref="IOException">The file is larger than the ceiling, or could not be read.</exception>
    internal static string AllText(string path, long ceilingBytes = DefaultCeilingBytes)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length > ceilingBytes)
        {
            throw new IOException(
                Path.GetFileName(path) + " is " + stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " bytes, over the " + ceilingBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " byte ceiling, so it is not a file this app wrote");
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}