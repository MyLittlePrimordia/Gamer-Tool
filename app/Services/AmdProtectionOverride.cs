using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace GamerTool.Services;

/// <summary>
/// Where AMD's per-display HDCP setting turned up, if anywhere.
/// <para>
/// A registry read only. It never touches the I2C bus, and it is only ever called
/// after a display has already failed, so it cannot be the thing that upsets a
/// monitor.
/// </para>
/// </summary>
public enum ProtectionOverrideState
{
    /// <summary>The registry could not be walked. No claim is made about the setting.</summary>
    Unknown,

    /// <summary>No per-display protection override exists, so the driver is on its default.</summary>
    None,

    /// <summary>An override exists where this driver generation looks for it.</summary>
    Current,

    /// <summary>
    /// An override exists only under the legacy namespace. This is the case worth
    /// knowing about: the setting is saved, Adrenalin reports it as saved, and the
    /// driver never reads it.
    /// </summary>
    LegacyOnly
}


/// <summary>One display class key's worth of findings, for the log and the tests.</summary>
public sealed class ProtectionOverrideReading
{
    public ProtectionOverrideState State { get; init; } = ProtectionOverrideState.Unknown;

    /// <summary>The adapter's display class subkey, "0000" and so on. Empty when unknown.</summary>
    public string AdapterKey { get; init; } = string.Empty;

    /// <summary>The legacy DAL2 location that holds the value, relative to the class key.</summary>
    public string LegacyPath { get; init; } = string.Empty;

    /// <summary>
    /// The per-display key the current driver generation uses, relative to the
    /// class key. This is what proves the driver has somewhere current to read,
    /// and therefore that the absence of the value there means something.
    /// </summary>
    public string CurrentPath { get; init; } = string.Empty;

    /// <summary>
    /// The raw override bytes. Reported because their meaning is not documented by
    /// AMD, and a bug report that says "1,0,0,0,1,0,0,0" is worth more than one
    /// that says "protection is on".
    /// </summary>
    public byte[]? Value { get; init; }

    public string Summary =>
        State switch
        {
            ProtectionOverrideState.Unknown => "could not be read",
            ProtectionOverrideState.None => "no override saved, driver default in use",
            ProtectionOverrideState.Current => "saved where this driver reads it"
                + (Value is null ? string.Empty : " (" + Format(Value) + ")"),
            _ => "saved under the legacy tree only, which this driver does not read"
                + (Value is null ? string.Empty : " (" + Format(Value) + ")")
                + (CurrentPath.Length == 0 ? string.Empty : "; current per-display key is " + CurrentPath)
        };

    internal static string Format(byte[] value) => string.Join(",", value);
}


/// <summary>
/// Finds out whether AMD's per-display HDCP override is actually in a place the
/// installed driver will read it.
/// <para>
/// The reason this file exists. Adrenalin's HDCP Support toggle is the one
/// confirmed fix for a display that answers with a physical monitor and no DDC/CI
/// handle, and it is a good fix when it works. On the machine this was written
/// for it demonstrably was not working, and the registry says why: the override
/// was present under
/// <c>DAL2_DATA__2_0\DisplayPath_8\EDID_E305_A610\Option\ProtectionControl</c>,
/// while the same driver kept its live per-display state under
/// <c>DAL3_DATA\common\EDID_58117_42512_AG276QZD2_8</c>, where no
/// ProtectionControl value exists at all.
/// <para>
/// That accounts for the whole observed behaviour without needing anything else to
/// have gone wrong. The display worked for a few minutes after the toggle was
/// turned, which is what happens when a setting is pushed into a running driver
/// and then dropped on the next display re-initialisation. It came back blocked
/// after no restart, no driver update, no Windows update and no crash, and it was
/// still blocked after three reboots that Adrenalin itself initiated, with the
/// registry value unchanged throughout.
/// <para>
/// What is deliberately not claimed here is what the bytes mean. Community scripts
/// that set this value to disable protection are not AMD documentation, so the
/// state is reported as a location rather than as a verdict, and it goes into the
/// log and Copy diagnostics rather than into advice.
/// <para>
/// It is worth being explicit about the shape of that decision, because it was
/// briefly the other way round. There was a dialog that offered these steps to
/// anyone whose display could not be reached, and it was confidently wrong: on the
/// machine this was written for it sent the user through four restarts chasing a
/// setting that was already correct, and the one time the feature had worked it
/// had worked with no registry change at all. There is no way to prove a fix
/// works from inside the app, and a hint that turns out to be wrong is worse than
/// no hint, because the only way to tell the difference is to have followed it.
/// So the finding is recorded and the feature is hidden when it cannot work, and
/// nobody is told what to go and change.
/// </para>
/// </para>
/// </summary>
public static class AmdProtectionOverride
{
    /// <summary>
    /// The display adapter setup class. Every AMD adapter registers a numbered
    /// subkey here, and the per-display trees hang off those.
    /// </summary>
    private const string DisplayClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Where this driver generation keeps its live per-display state.</summary>
    private const string CurrentTree = @"DAL3_DATA\common";

    /// <summary>Where the HDCP override has been found, on drivers that still read it.</summary>
    private const string LegacyTree = @"DAL2_DATA__2_0";

    /// <summary>The value name AMD uses for the per-display content protection setting.</summary>
    private const string ValueName = "ProtectionControl";

    private const string EdidPrefix = "EDID_";

    private static ProtectionOverrideReading? _cached;

    /// <summary>
    /// The reading for the machine, walked once. The registry walk is a handful of
    /// key opens and this is only needed when a display has already failed, but a
    /// probe can be triggered by every tab switch and there is no reason to pay for
    /// it twice.
    /// </summary>
    public static ProtectionOverrideReading Read()
    {
        if (_cached is { } known)
        {
            return known;
        }

        ProtectionOverrideReading reading = Walk();
        _cached = reading;
        return reading;
    }

    /// <summary>
    /// Reads the registry for the answer, every time. Read() is what caches.
    /// </summary>
    /// <para>
    /// The summary here used to describe forgetting the cached reading, which is
    /// the absence of a method rather than one: nothing here invalidates anything,
    /// and a comment saying so next to the only method on the class reads as a
    /// description of what it does. It matters because the cache has no way out at
    /// all, so somebody reading this would reasonably assume one existed.
    /// </para>
    /// </summary>
    private static ProtectionOverrideReading Walk()
    {
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (root is null)
            {
                return new ProtectionOverrideReading();
            }

            // A machine with two AMD adapters has one class subkey each, and only
            // the one driving the display has a per-display tree under it. Every
            // subkey is walked and the strongest answer wins, rather than returning
            // the first adapter that says anything at all. That distinction is the
            // whole point of the file: on the machine this was written for, adapter
            // 0000 holds both the legacy override and the current per-display key
            // while 0001, the other AMD adapter, holds neither. Stopping at 0001
            // because it was reached first reported "could not be read" and threw
            // away the finding.
            ProtectionOverrideReading best = new();

            // Only the numbered adapter subkeys. The same root also holds
            // "Configuration" and "Properties", and Properties throws
            // SecurityException when it is opened, which would abort the whole walk
            // and report "could not be read" on a machine whose answer was sitting
            // right there in 0000.
            foreach (string adapter in root.GetSubKeyNames()
                         .Where(IsAdapterKey)
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                ProtectionOverrideReading? found = ReadAdapter(root, adapter);
                if (found is not null && Rank(found.State) > Rank(best.State))
                {
                    best = found;
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            // Diagnostics must never be the thing that breaks a probe.
            TraceLog.Write("AMD protection override read failed: " + ex.GetType().Name);
            return new ProtectionOverrideReading();
        }
    }

    /// <summary>
    /// One adapter's answer, or null when this adapter has nothing to say and the
    /// next one should be tried.
    /// </summary>
    private static ProtectionOverrideReading? ReadAdapter(RegistryKey root, string adapter)
    {
        using RegistryKey? adapterKey = root.OpenSubKey(adapter);
        if (adapterKey is null)
        {
            return null;
        }

        string legacyPath = string.Empty;
        byte[]? legacyValue = null;

        // Opened from the tree, and every display path below is opened from this
        // key rather than from the adapter. The display paths are children of
        // DAL2_DATA__2_0, not of the adapter, so asking the adapter for
        // "DisplayPath_8" finds nothing and the whole legacy side reads as empty
        // on a machine that has the value sitting in it.
        using (RegistryKey? legacyRoot = adapterKey.OpenSubKey(LegacyTree))
        {
            if (legacyRoot is not null)
            {
                foreach (string path in DisplayPaths(legacyRoot))
                {
                    using RegistryKey? displayPath = legacyRoot.OpenSubKey(path);
                    if (displayPath is null)
                    {
                        continue;
                    }

                    foreach (string edid in EdidKeys(displayPath))
                    {
                        // The value is in a child of the EDID key, and is read from
                        // there directly rather than through a path built by
                        // concatenation.
                        using RegistryKey? option = displayPath.OpenSubKey(edid + @"\Option");
                        if (option?.GetValue(ValueName) is not byte[] bytes || bytes.Length == 0)
                        {
                            continue;
                        }

                        legacyPath = path + "\\" + edid + "\\Option";
                        legacyValue = bytes;
                        break;
                    }

                    if (legacyPath.Length > 0)
                    {
                        break;
                    }
                }
            }
        }

        string currentPath = string.Empty;
        byte[]? currentValue = null;

        using (RegistryKey? currentRoot = adapterKey.OpenSubKey(CurrentTree))
        {
            if (currentRoot is not null)
            {
                foreach (string edid in EdidKeys(currentRoot).OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (currentPath.Length == 0)
                    {
                        currentPath = CurrentTree + "\\" + edid;
                    }

                    // The value is looked for on the key itself and one level down.
                    // Which of the two this driver uses is not documented, and
                    // guessing one of them would turn a real finding into a silent
                    // None.
                    foreach (string candidate in new[] { edid, edid + "\\Option" })
                    {
                        byte[]? value = ReadValue(currentRoot, candidate, ValueName);
                        if (value is not null)
                        {
                            currentValue = value;
                            currentPath = CurrentTree + "\\" + candidate;
                            break;
                        }
                    }

                    if (currentValue is not null)
                    {
                        break;
                    }
                }
            }
        }

        ProtectionOverrideState state = Classify(legacyPath.Length > 0, currentValue is not null, currentPath.Length > 0);

        // Nothing at all to report: let the caller try the next adapter rather than
        // answering None on behalf of a card that was never really looked at.
        if (state == ProtectionOverrideState.None)
        {
            return null;
        }

        return new ProtectionOverrideReading
        {
            State = state,
            AdapterKey = adapter,
            LegacyPath = legacyPath.Length == 0 ? string.Empty : LegacyTree + "\\" + legacyPath,
            CurrentPath = currentPath,
            Value = currentValue ?? legacyValue
        };
    }

    /// <summary>
    /// Whether a subkey name is one of the numbered adapter slots, which are four
    /// digits. Pure, so the filter that keeps a denied key from ending the walk can
    /// be pinned by a test rather than only by a machine that happens to have one.
    /// </summary>
    public static bool IsAdapterKey(string name)
    {
        if (name.Length != 4)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// How much a state is worth saying, when more than one adapter has an opinion.
    /// A definite finding beats an absence of one, and LegacyOnly is the most
    /// definite answer there is because it names two locations and says one of them
    /// is not being read.
    /// </summary>
    private static int Rank(ProtectionOverrideState state) => state switch
    {
        ProtectionOverrideState.LegacyOnly => 3,
        ProtectionOverrideState.Current => 2,
        ProtectionOverrideState.None => 1,
        _ => 0
    };

    /// <summary>
    /// The decision, kept pure and separate so it can be pinned by tests rather
    /// than only by whatever machine happens to be running them.
    /// </summary>
    public static ProtectionOverrideState Classify(
        bool legacyFound,
        bool currentFound,
        bool currentKeyPresent)
    {
        if (currentFound)
        {
            return ProtectionOverrideState.Current;
        }

        // Without a current per-display key there is nowhere for the driver to read
        // an override from, so finding one in the legacy tree says nothing about
        // whether it is being honoured. Saying "legacy only" here would be
        // accusing a driver of ignoring a setting on the strength of an absence
        // that was never really an absence.
        if (legacyFound && currentKeyPresent)
        {
            return ProtectionOverrideState.LegacyOnly;
        }

        if (legacyFound)
        {
            return ProtectionOverrideState.Unknown;
        }

        // Nothing anywhere, and nowhere for the driver to keep a setting either.
        // That is a machine with no AMD per-display tree at all, so there is no
        // driver to have a default: Unknown, not None.
        return currentKeyPresent ? ProtectionOverrideState.None : ProtectionOverrideState.Unknown;
    }

    /// <summary>
    /// The display path subkeys under an already-opened tree. Takes the tree
    /// rather than the adapter, because the paths are children of the tree.
    /// </summary>
    private static IEnumerable<string> DisplayPaths(RegistryKey treeKey) =>
        treeKey.GetSubKeyNames()
            .Where(n => n.StartsWith("DisplayPath", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<string> EdidKeys(RegistryKey parent) =>
        parent.GetSubKeyNames()
            .Where(n => n.StartsWith(EdidPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static byte[]? ReadValue(RegistryKey parent, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? key = parent.OpenSubKey(subKey);
            if (key?.GetValue(valueName) is not byte[] bytes || bytes.Length == 0)
            {
                return null;
            }

            return bytes;
        }
        catch (Exception ex)
        {
            TraceLog.Write("AMD protection override value read failed: " + ex.GetType().Name);
            return null;
        }
    }
}
