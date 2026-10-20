using System;
using System.Collections.Generic;
using System.IO;

namespace GamerTool.Services;

/// <summary>
/// Whether the executable named as the audio engine is one this app is willing
/// to start.
/// <para>
/// The engine path arrives from two places nobody types: settings.json and a
/// backup file that may have been sent from another machine. Both are executable
/// paths, and the app launches one of them several times a second - silently, as
/// this user. Checking the file name, which is what <see
/// cref="AudioService.IsPlausibleFxSoundPath"/> does, stops a profile naming
/// <c>payload.exe</c>; it does not stop a profile naming a real
/// <c>FxSound.exe</c> that is not FxSound's.
/// </para>
/// <para>
/// Two tiers, because the two cases cost very different amounts and deserve
/// different answers:
/// </para>
/// <para>
/// **Program Files is trusted by location.** Writing there needs an
/// administrator, so a non-elevated program - which this is - cannot plant a
/// file there. This is the default install and the common case, and it costs a
/// string comparison rather than an Authenticode probe.
/// </para>
/// <para>
/// **Anywhere else needs a signature from FxSound.** FxSound installs to a
/// custom folder perfectly legitimately, so the folder cannot be the rule. The
/// signer is: the same check the installer already goes through, so a genuine
/// custom install passes and a renamed system binary does not.
/// </para>
/// <para>
/// UNC and extended-length paths are refused outright, before either tier. A
/// path beginning <c>\\</c> names a share another machine controls, and
/// <c>\\?\</c> is a way to hand Windows a path its own normalisation never sees -
/// neither is a place the engine is installed, and verifying a file over SMB is
/// not something to do on a timer.
/// </para>
/// </summary>
internal static class FxSoundTrust
{
    /// <summary>Verdicts already paid for, by path and the file's identity.</summary>
    private static readonly Dictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>
    /// How many verdicts are kept. A profile has one engine path, so this is only
    /// reached by somebody editing the file repeatedly; the cap is here so that
    /// cannot grow without bound.
    /// </summary>
    private const int CacheLimit = 32;

    private readonly record struct Cached(long Size, long WrittenAtTicks, bool Trusted);

    /// <summary>Whether a path is in a place that is trusted without a signature.</summary>
    internal static bool IsTrustedLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Before normalisation, because the extended form is exactly the path
        // normalisation would rewrite into something that looks ordinary.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }

            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }

        foreach (string root in ProgramFilesRoots())
        {
            if (root.Length > 0 && IsUnder(full, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the app may start this executable as the engine.
    /// <para>
    /// False for a missing file: an imported path that is not on this machine is
    /// not something to launch, and the caller's repair treats it as a path to
    /// replace.
    /// </para>
    /// </summary>
    internal static bool IsTrusted(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }

        if (!File.Exists(full))
        {
            return false;
        }

        if (IsTrustedLocation(full))
        {
            return true;
        }

        return HasTrustedSignature(full);
    }

    /// <summary>
    /// The Program Files folders this machine actually has, as full paths.
    /// <para>
    /// Both are asked for rather than assuming x64: a 32-bit install is a
    /// supported configuration and its engine lives in Program Files (x86).
    /// </para>
    /// </summary>
    private static IEnumerable<string> ProgramFilesRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    /// <summary>
    /// True when <paramref name="path"/> is inside <paramref name="root"/> and not
    /// merely sharing a prefix with it. <c>C:\Program Files Eviltools</c> starts
    /// with <c>C:\Program Files</c> and is not inside it.
    /// </summary>
    private static bool IsUnder(string path, string root)
    {
        string normalRoot;
        try
        {
            normalRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
        }
        catch (Exception)
        {
            return false;
        }

        if (path.Length <= normalRoot.Length
            || !path.StartsWith(normalRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        char after = path[normalRoot.Length];
        return after == '\\' || after == '/';
    }

    /// <summary>
    /// The signature verdict, cached against the file's size and write time.
    /// <para>
    /// The probe is the expensive part - it opens the file, validates the chain
    /// and reads a certificate - and it would otherwise run on every command the
    /// app sends to a custom install, which is several times a second while a
    /// fader moves. Size and write time are what identify a file for this
    /// purpose: replacing it changes at least one of them, and a replaced file
    /// gets a fresh verdict.
    /// </para>
    /// </summary>
    private static bool HasTrustedSignature(string full)
    {
        long size;
        long written;
        try
        {
            FileInfo info = new(full);
            size = info.Length;
            written = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex)
        {
            TraceLog.Write("FX TRUST could not read " + full, ex);
            return false;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(full, out Cached cached)
                && cached.Size == size
                && cached.WrittenAtTicks == written)
            {
                return cached.Trusted;
            }
        }

        bool trusted = SetupService.IsSignedByPublisher(full, SetupService.PublisherHint);

        lock (Gate)
        {
            if (Cache.Count >= CacheLimit)
            {
                Cache.Clear();
            }

            Cache[full] = new Cached(size, written, trusted);
        }

        TraceLog.Write("FX TRUST " + full + " is " + (trusted ? "signed by" : "not signed by") + " the publisher");
        return trusted;
    }
}
