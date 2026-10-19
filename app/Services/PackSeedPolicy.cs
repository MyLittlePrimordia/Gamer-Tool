using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

namespace GamerTool.Services;

/// <summary>What to do with one starter picture when the pack folder is filled.</summary>
internal enum SeedAction
{
    /// <summary>Do not touch the file, or do not bring it back.</summary>
    Leave,

    /// <summary>Write the shipped copy.</summary>
    Write
}

/// <summary>
/// The rules for keeping the crosshair pack's starter pictures up to date without
/// ever overwriting something the user did.
/// <para>
/// The folder is filled from pictures inside the program. Until now a picture was
/// written once and never again, so a starter picture that was corrected in a later
/// version never reached anyone who already had the folder. The fix is to remember,
/// for every starter picture, a fingerprint of the bytes this program last wrote.
/// A file on disk that still matches that fingerprint has not been touched by the
/// user, so it is safe to replace with the corrected one. A file that does not match
/// was edited or swapped by the user and is left alone, and a file that is missing
/// after having been written was deleted by the user and stays deleted.
/// </para>
/// <para>
/// Pure functions on strings, so every one of those rules has a test.
/// </para>
/// </summary>
internal static class PackSeedPolicy
{
    /// <summary>The fingerprint of a picture's bytes.</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// Decide what to do with one starter picture.
    /// </summary>
    /// <param name="onDisk">The file is in the folder.</param>
    /// <param name="seededBefore">The marker has an entry for it, so this program wrote it at some point.</param>
    /// <param name="lastWritten">The fingerprint the marker holds. Empty when the file was already the user's the first time it was seen.</param>
    /// <param name="diskHash">The fingerprint of what is on disk. Only needed when it could change the answer; null otherwise.</param>
    /// <param name="shippedHash">The fingerprint of the copy inside the program.</param>
    public static SeedAction Decide(bool onDisk, bool seededBefore, string? lastWritten, string? diskHash, string shippedHash)
    {
        if (!onDisk)
        {
            // Never written: bring it. Written and now gone: the user took it away.
            return seededBefore ? SeedAction.Leave : SeedAction.Write;
        }

        // Replace only a file this program wrote and nobody has touched, and only
        // when the copy inside the program has since changed.
        bool untouched = seededBefore
            && !string.IsNullOrEmpty(lastWritten)
            && string.Equals(diskHash, lastWritten, StringComparison.Ordinal);

        bool outOfDate = !string.Equals(lastWritten, shippedHash, StringComparison.Ordinal);

        return untouched && outOfDate ? SeedAction.Write : SeedAction.Leave;
    }

    /// <summary>
    /// What the marker should hold for the picture afterwards. The shipped
    /// fingerprint when it was written, or when it was already on disk and is
    /// identical to what ships. The old entry when the file was left alone. Empty
    /// for a file that was there before the marker knew about it and differs from
    /// what ships: it is the user's, and an empty entry is how it stays that way.
    /// </summary>
    public static string Record(SeedAction action, bool onDisk, bool seededBefore, string? lastWritten, string? diskHash, string shippedHash)
    {
        if (action == SeedAction.Write)
        {
            return shippedHash;
        }

        if (seededBefore)
        {
            return lastWritten ?? string.Empty;
        }

        return onDisk && string.Equals(diskHash, shippedHash, StringComparison.Ordinal)
            ? shippedHash
            : string.Empty;
    }

    /// <summary>
    /// Read the marker. The first versions stored a single hash of the file names,
    /// which is not JSON: that reads as no history at all, which is safe, since no
    /// history means every file already on disk is left as it is.
    /// </summary>
    public static Dictionary<string, string> ParseMarker(string? text)
    {
        Dictionary<string, string> empty = new(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(text))
        {
            return empty;
        }

        try
        {
            Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (parsed is null)
            {
                return empty;
            }

            Dictionary<string, string> clean = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in parsed)
            {
                clean[entry.Key] = entry.Value ?? string.Empty;
            }

            return clean;
        }
        catch (JsonException)
        {
            return empty;
        }
    }

    public static string SerializeMarker(IReadOnlyDictionary<string, string> marker)
    {
        // Sorted, so the file does not change from one launch to the next and is
        // not rewritten when nothing did.
        SortedDictionary<string, string> sorted = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in marker)
        {
            sorted[entry.Key] = entry.Value;
        }

        return JsonSerializer.Serialize(sorted);
    }
}
