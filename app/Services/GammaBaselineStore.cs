using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GamerTool.Services;

/// <summary>
/// Remembers what each screen's gamma ramp was before this app changed it, so a
/// run that did not exit cleanly can be undone on the next one.
/// <para>
/// <see cref="DisplayService"/> already keeps the original ramps, but only in
/// memory. A crash, a Task Manager kill or a power cut takes them with it, and
/// the next launch then reads the <em>tinted</em> table back as the original -
/// which makes the tint permanent, because every later Reset restores the tint
/// and calls it normal. That is the failure this file exists to stop being
/// permanent: the ramps go to disk before the first hardware write, with a dirty
/// flag that says they may still be on screen.
/// </para>
/// <para>
/// The dirty flag is only acted on when it was set during the current boot. A
/// reboot clears the driver's gamma table anyway, so a marker older than the
/// boot is evidence of nothing and is cleared rather than written back over
/// whatever the machine came up with.
/// </para>
/// <para>
/// Keyed by the panel's PnP id rather than by <c>\\.\DISPLAYn</c>, because
/// Windows renumbers display names when a cable moves and a baseline filed under
/// the old number would be restored onto different hardware. The device name is
/// still consulted as a fallback, because an adapter can fail to report a
/// monitor id.
/// </para>
/// </summary>
internal sealed class GammaBaselineStore
{
    /// <summary>Entries per channel. The Win32 gamma table is 256 per channel.</summary>
    internal const int RampSize = 256;

    /// <summary>Values in one stored ramp: three channels laid end to end.</summary>
    internal const int Values = RampSize * 3;

    private const int StoredVersion = 1;

    /// <summary>One monitor's ramps are 1.5 KB, so this is many monitors, not many files.</summary>
    private const long CeilingBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    private readonly Func<DateTime> _clock;

    private readonly object _gate = new();

    private readonly Dictionary<string, ushort[]> _ramps = new(StringComparer.OrdinalIgnoreCase);

    private bool _dirty;

    private DateTime _dirtySinceUtc;

    /// <param name="path">Overridden by the tests; the app uses the profile folder.</param>
    /// <param name="clock">
    /// The source of "now". Injected so the boot comparison can be exercised
    /// without waiting for a reboot.
    /// </param>
    public GammaBaselineStore(string? path = null, Func<DateTime>? clock = null)
    {
        _path = path ?? Path.Combine(ProfileManager.AppDataFolder, "display-baseline.json");
        _clock = clock ?? (() => DateTime.UtcNow);
        Load();
    }

    public string Path_ => _path;

    /// <summary>
    /// True while the ramps in this file may still be on screen, which is to say
    /// the run that wrote them never got to put the originals back.
    /// </summary>
    public bool Dirty
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    /// <summary>True when nothing has ever been captured, which is a fresh install.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _ramps.Count == 0;
            }
        }
    }

    /// <summary>
    /// Whether a dirty marker was written after the machine last started.
    /// <para>
    /// A marker from an earlier boot is not acted on: Windows has reset the
    /// gamma table since, and restoring an old baseline would overwrite whatever
    /// the user or another tool has set up in the meantime. The caller clears the
    /// flag in that case.
    /// </para>
    /// </summary>
    public bool DirtyAppliesToThisBoot(DateTime nowUtc, TimeSpan uptime)
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return false;
            }

            DateTime bootedAt = nowUtc - uptime;
            return _dirtySinceUtc > bootedAt;
        }
    }

    /// <summary>
    /// Records a screen's baseline and flushes it with the dirty flag set.
    /// <para>
    /// Called before the hardware write, not after it, because the write is the
    /// thing that can be interrupted. After the first call the flag is already
    /// set and the entry already matches, so the common path is a comparison and
    /// no I/O: the gamma lock can call this on every push without turning into a
    /// disk write every time.
    /// </para>
    /// </summary>
    public void BeginDirty(string key, ushort[] ramp)
    {
        if (ramp.Length != Values)
        {
            return;
        }

        lock (_gate)
        {
            bool known = _ramps.TryGetValue(key, out ushort[]? existing)
                && existing.AsSpan().SequenceEqual(ramp);

            if (known && _dirty)
            {
                return;
            }

            _ramps[key] = (ushort[])ramp.Clone();
            _dirty = true;
            _dirtySinceUtc = _clock();
            Save();
        }
    }

    /// <summary>Records that the screen is back to normal, if it was ever touched.</summary>
    public void MarkClean()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            Save();
        }
    }

    /// <summary>
    /// The baseline filed for a screen, by PnP id first and device name second.
    /// Null means this app never captured that screen, which is not the same as
    /// having captured an identity ramp.
    /// </summary>
    public ushort[]? Find(string key, string device)
    {
        lock (_gate)
        {
            if (_ramps.TryGetValue(key, out ushort[]? ramp))
            {
                return (ushort[])ramp.Clone();
            }

            if (_ramps.TryGetValue(device, out ramp))
            {
                return (ushort[])ramp.Clone();
            }

            return null;
        }
    }

    /// <summary>
    /// A flat, full-range table: output equals input at every step.
    /// <para>
    /// The fallback when the file says a ramp was left on screen but carries no
    /// baseline to put back. Restoring identity is the only answer that cannot
    /// make things worse: it is what Windows uses when nothing has set a ramp, and
    /// it is certainly better than leaving a crashed session's tint in place.
    /// </para>
    /// </summary>
    internal static ushort[] Identity()
    {
        ushort[] ramp = new ushort[Values];

        for (int i = 0; i < RampSize; i++)
        {
            ushort value = (ushort)(i * 257);
            ramp[i] = value;
            ramp[RampSize + i] = value;
            ramp[(RampSize * 2) + i] = value;
        }

        return ramp;
    }

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

            foreach (KeyValuePair<string, string> pair in stored.Ramps ?? new())
            {
                ushort[]? ramp = Decode(pair.Value);
                if (ramp is not null)
                {
                    _ramps[pair.Key] = ramp;
                }
            }

            _dirty = stored.Dirty;

            // RoundtripKind, because the field is written as "O" and a marker
            // read back as local time would be compared against a UTC boot time
            // with an offset-sized error - which is exactly the size that decides
            // the question on a machine that has been up for an hour.
            if (DateTime.TryParse(
                stored.DirtySinceUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTime since))
            {
                _dirtySinceUtc = since.ToUniversalTime();
            }
        }
        catch (Exception ex)
        {
            // A file that will not parse is not evidence of a ramp on screen, and
            // acting on half of one is worse than starting clean. The next apply
            // writes a fresh baseline.
            _ramps.Clear();
            _dirty = false;
            AppLog.Error("DISPLAY BASELINE LOAD", ex);
        }
    }

    private void Save()
    {
        try
        {
            Stored stored = new()
            {
                Version = StoredVersion,
                Dirty = _dirty,
                DirtySinceUtc = _dirtySinceUtc == default
                    ? string.Empty
                    : _dirtySinceUtc.ToString("O", CultureInfo.InvariantCulture)
            };

            foreach (KeyValuePair<string, ushort[]> pair in _ramps)
            {
                stored.Ramps[pair.Key] = Encode(pair.Value);
            }

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
            // Not fatal: the in-memory ramps are still the ones the session uses,
            // and a failure to persist only means the next crash is back to the
            // old behaviour for this run.
            AppLog.Error("DISPLAY BASELINE SAVE", ex);
        }
    }

    /// <summary>
    /// Little-endian, three channels end to end, Base64. That is the shape the
    /// previous build wrote, so a file it left behind is read rather than
    /// discarded.
    /// </summary>
    private static string Encode(ushort[] ramp)
    {
        byte[] bytes = new byte[ramp.Length * 2];

        for (int i = 0; i < ramp.Length; i++)
        {
            bytes[i * 2] = (byte)(ramp[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)(ramp[i] >> 8);
        }

        return Convert.ToBase64String(bytes);
    }

    private static ushort[]? Decode(string value)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            if (bytes.Length != Values * 2)
            {
                return null;
            }

            ushort[] ramp = new ushort[Values];
            for (int i = 0; i < Values; i++)
            {
                ramp[i] = (ushort)(bytes[i * 2] | (bytes[(i * 2) + 1] << 8));
            }

            return ramp;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class Stored
    {
        public int Version { get; set; } = StoredVersion;

        public bool Dirty { get; set; }

        public string DirtySinceUtc { get; set; } = string.Empty;

        public Dictionary<string, string> Ramps { get; set; } = new();
    }
}
