using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GamerTool.Services;

/// <summary>
/// The names used in the journal. Strings rather than enum numbers, because the
/// file has to be readable by a later build and by a person looking at it after a
/// crash. The enum is the in-memory shape; this is the on-disk one.
/// </summary>
public static class SystemRestoreKinds
{
    public const string AudioPlayback = "audio-playback";

    public const string AudioPlaybackCommunications = "audio-playback-comms";

    public const string AudioCapture = "audio-capture";

    public const string AudioCaptureCommunications = "audio-capture-comms";

    public const string Hdr = "hdr";

    public static string For(WindowsAudioFlow flow) => flow switch
    {
        WindowsAudioFlow.Playback => AudioPlayback,
        WindowsAudioFlow.PlaybackCommunications => AudioPlaybackCommunications,
        WindowsAudioFlow.Recording => AudioCapture,
        _ => AudioCaptureCommunications,
    };

    public static bool TryFlow(string kind, out WindowsAudioFlow flow)
    {
        switch (kind)
        {
            case AudioPlayback:
                flow = WindowsAudioFlow.Playback;
                return true;
            case AudioPlaybackCommunications:
                flow = WindowsAudioFlow.PlaybackCommunications;
                return true;
            case AudioCapture:
                flow = WindowsAudioFlow.Recording;
                return true;
            case AudioCaptureCommunications:
                flow = WindowsAudioFlow.RecordingCommunications;
                return true;
            default:
                flow = default;
                return false;
        }
    }
}

/// <summary>One system setting this app changed and has not put back yet.</summary>
public sealed class SystemRestoreEntry
{
    public string Kind { get; set; } = string.Empty;

    /// <summary>The endpoint role, or the monitor key for HDR.</summary>
    public string Target { get; set; } = string.Empty;

    public string Before { get; set; } = string.Empty;

    public string After { get; set; } = string.Empty;

    public override string ToString() => Kind + " " + Target;
}

/// <summary>
/// The changes this app makes that survive a reboot, written to disk before they
/// are made and cleared only after a verified restore.
/// <para>
/// The gamma baseline has its own store and its own rule, and this deliberately
/// does not copy it. Windows resets the gamma table at boot, so an old dirty
/// marker is evidence of nothing. Audio defaults and HDR persist across reboots,
/// which means an entry here is still true after a crash, a Task Manager kill or
/// a power cut - and the next launch is the only thing that can put it back.
/// </para>
/// <para>
/// First capture wins for <see cref="SystemRestoreEntry.Before"/> and the last
/// apply wins for <see cref="SystemRestoreEntry.After"/>, exactly like the audio
/// route memory. An entry is removed only when the caller has verified the value
/// is back, or has established that the user changed it themselves.
/// </para>
/// </summary>
public sealed class SystemRestoreJournal
{
    private const int StoredVersion = 1;

    /// <summary>Four routes plus a handful of monitors is a few kilobytes.</summary>
    private const long CeilingBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    private readonly object _gate = new();

    private readonly List<SystemRestoreEntry> _entries = new();

    /// <summary>The journal the running app uses. Lazy, because tests never touch it.</summary>
    public static SystemRestoreJournal Default { get; } = new();

    /// <param name="path">Overridden by the tests; the app uses the profile folder.</param>
    public SystemRestoreJournal(string? path = null)
    {
        _path = path ?? Path.Combine(ProfileManager.AppDataFolder, "pending-restore.json");
        Load();
    }

    internal string Path_ => _path;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool IsEmpty => Count == 0;

    public IReadOnlyList<SystemRestoreEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries
                    .Select(entry => new SystemRestoreEntry
                    {
                        Kind = entry.Kind,
                        Target = entry.Target,
                        Before = entry.Before,
                        After = entry.After,
                    })
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Adds the first capture or updates the latest applied value.
    /// <para>
    /// Writing is synchronous and happens on the apply path, before the hardware
    /// is touched. The alternative - writing afterwards - loses the record of
    /// exactly the run that was interrupted between the two.
    /// </para>
    /// </summary>
    public void Record(string kind, string target, string before, string after)
    {
        if (string.IsNullOrWhiteSpace(kind)
            || string.IsNullOrWhiteSpace(target)
            || string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_gate)
        {
            SystemRestoreEntry? existing = Find(kind, target);

            if (existing is null)
            {
                _entries.Add(new SystemRestoreEntry
                {
                    Kind = kind,
                    Target = target,
                    Before = before,
                    After = after,
                });

                Save();
                return;
            }

            // First-before, last-after. If the last apply landed back on the
            // original value there is nothing left to undo, and keeping the entry
            // would make the next launch write a value the user is already on.
            existing.After = after;

            if (string.Equals(existing.Before, existing.After, StringComparison.OrdinalIgnoreCase))
            {
                _entries.Remove(existing);
            }

            Save();
        }
    }

    /// <summary>A copy of one entry, or false when this kind and target have none.</summary>
    public bool TryGet(string kind, string target, out SystemRestoreEntry entry)
    {
        lock (_gate)
        {
            SystemRestoreEntry? found = Find(kind, target);

            if (found is null)
            {
                entry = new SystemRestoreEntry();
                return false;
            }

            entry = new SystemRestoreEntry
            {
                Kind = found.Kind,
                Target = found.Target,
                Before = found.Before,
                After = found.After,
            };

            return true;
        }
    }

    /// <summary>Drops an entry. Call only after a verified restore or a known user change.</summary>
    public void Remove(string kind, string target)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(entry => Matches(entry, kind, target)) > 0)
            {
                Save();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
            Save();
        }
    }

    private SystemRestoreEntry? Find(string kind, string target) =>
        _entries.FirstOrDefault(entry => Matches(entry, kind, target));

    private static bool Matches(SystemRestoreEntry entry, string kind, string target) =>
        string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            string json = BoundedRead.AllText(_path, CeilingBytes);
            Stored? stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored is null || stored.Version != StoredVersion)
            {
                return;
            }

            foreach (SystemRestoreEntry entry in stored.Entries ?? new())
            {
                if (string.IsNullOrWhiteSpace(entry.Kind)
                    || string.IsNullOrWhiteSpace(entry.Target)
                    || string.Equals(entry.Before, entry.After, StringComparison.OrdinalIgnoreCase)
                    || Find(entry.Kind, entry.Target) is not null)
                {
                    continue;
                }

                _entries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            // A journal that will not parse is not evidence that anything is
            // still changed, and acting on half of one is worse than starting
            // clean. The next apply writes a fresh entry.
            _entries.Clear();
            AppLog.Error("SYSTEM RESTORE LOAD", ex);
        }
    }

    private void Save()
    {
        try
        {
            Stored stored = new() { Version = StoredVersion, Entries = _entries };

            string json = JsonSerializer.Serialize(stored, Options);
            string? folder = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            string temp = _path + ".tmp";
            using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            ProfileManager.AtomicSwap(temp, _path);
        }
        catch (Exception ex)
        {
            // Not fatal: the in-memory entries are still the ones this session
            // uses, and a failure to persist only means the next crash is back to
            // the old behaviour for this run.
            AppLog.Error("SYSTEM RESTORE SAVE", ex);
        }
    }

    private sealed class Stored
    {
        public int Version { get; set; } = StoredVersion;

        public List<SystemRestoreEntry> Entries { get; set; } = new();
    }
}
