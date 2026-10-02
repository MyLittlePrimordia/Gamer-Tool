using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class ProfileManager
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>How many unreadable settings files are kept for hand recovery.</summary>
    private const int KeepQuarantineFiles = 3;

    /// <summary>How many times a read is retried before it is called unreadable.</summary>
    private const int ReadAttempts = 3;

    /// <summary>The name that opts a folder in to being the profile's home.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// The folder holding the running executable, or null when it cannot be
    /// worked out.
    /// <para>
    /// <c>Environment.ProcessPath</c> rather than <c>AppContext.BaseDirectory</c>,
    /// which point at different places for a single file build. BaseDirectory is
    /// the extraction directory of the bundle for a self contained single file
    /// app, so a portable mode keyed off it would write into a temporary folder
    /// that is deleted on exit. The running executable's own folder is where the
    /// user put the file and is therefore where they would look for it.
    /// </para>
    /// </summary>
    public static string? ExecutableFolder
    {
        get
        {
            string? folder = Path.GetDirectoryName(Environment.ProcessPath);
            return string.IsNullOrWhiteSpace(folder) ? null : folder;
        }
    }

    /// <summary>
    /// Whether this is a portable install: a settings file sitting beside the
    /// executable.
    /// <para>
    /// Opt-in by the presence of the file, which is the only signal available
    /// that does not need somewhere else to record the decision. Nothing changes
    /// for anybody who has not done it, because nobody has: an existing install
    /// has its profile in the roaming folder and no settings file next to the
    /// executable, so this is false and stays false.
    /// </para>
    /// <para>
    /// Recomputed on every read rather than cached, because the file can be
    /// dropped in or removed while the app is running, and because caching it in
    /// a static would mean a test could not exercise it.
    /// </para>
    /// </summary>
    public static bool IsPortable
    {
        get
        {
            string? folder = ExecutableFolder;
            return folder is not null && File.Exists(Path.Combine(folder, SettingsFileName));
        }
    }

    /// <summary>
    /// Where the profile lives when nothing overrides it.
    /// <para>
    /// Computed rather than cached, so a portable install can be switched on by
    /// the presence of a file rather than by a flag that has to be remembered
    /// somewhere else.
    /// </para>
    /// </summary>
    public static string AppDataFolder
    {
        get
        {
            if (IsPortable && ExecutableFolder is { } beside)
            {
                return beside;
            }

            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "GamerTool");
        }
    }

    private readonly string _folder;

    /// <summary>
    /// Set when the settings file is there but could not be read, which means it
    /// is not known to be damaged. Saving is held off until the next launch so
    /// defaults cannot quietly replace work that is merely out of reach.
    /// </summary>
    private bool _unreadable;

    /// <summary>
    /// <paramref name="folder"/> exists so a test can work somewhere disposable.
    /// The default is the real profile, which is what the app uses.
    /// </summary>
    public ProfileManager(string? folder = null)
    {
        _folder = string.IsNullOrWhiteSpace(folder) ? AppDataFolder : folder!;
        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch (Exception ex)
        {
            // A portable install on a drive or folder that cannot be written to.
            // Starting must not depend on being able to write, so this is logged
            // and the app comes up on defaults; Save reports its own failure
            // every time it is tried, which is the honest place for it to surface.
            AppLog.Error("SETTINGS FOLDER", ex);
        }
    }

    public string SettingsPath => Path.Combine(_folder, SettingsFileName);

    /// <summary>The last known good copy, which the atomic swap leaves behind.</summary>
    private string BackupPath => SettingsPath + ".bak";

    /// <summary>
    /// True when the profile came back from the backup rather than from the file
    /// itself. The window says so once, because a profile that came from anywhere
    /// other than where the user left it is worth knowing about.
    /// </summary>
    public bool RecoveredFromBackup { get; private set; }

    /// <summary>
    /// True when the settings file was unreadable as content and has been moved
    /// aside. The file itself is still on disk under a timestamped name.
    /// </summary>
    public bool Quarantined { get; private set; }

    /// <summary>
    /// Brings the profile on disk into the app, or falls back to one that is.
    /// <para>
    /// The distinction this method exists to make is between a file that is
    /// damaged and a file that could not be read. Both arrive as an exception, and
    /// treating them the same was how a healthy profile got thrown away: an
    /// antivirus scan, a sync client or a backup tool holding the file open for a
    /// moment was enough to land here, and the old code moved a perfectly good
    /// settings.json aside as corrupt and then let the next save write defaults
    /// over it. Every preset, slot and hotkey went with it, with nothing on screen
    /// to explain why.
    /// </para>
    /// <para>
    /// So a read that fails is retried with a sharing mode that tolerates another
    /// reader, and if it still fails the file is left exactly where it is and
    /// saving is held off. Only content that will not parse is treated as damage:
    /// that file is moved aside so the evidence survives, and the copy the atomic
    /// swap left behind is tried before giving up on the session's work.
    /// </para>
    /// </summary>
    public AppSettings Load()
    {
        _unreadable = false;
        RecoveredFromBackup = false;
        Quarantined = false;

        string? json = TryRead(SettingsPath, out bool unreadable);
        if (unreadable)
        {
            _unreadable = true;
            AppLog.Error("SETTINGS LOAD", new IOException(
                "settings.json is present but could not be read; it has been left alone"));
            return Normalize(new AppSettings());
        }

        if (json is not null)
        {
            if (TryParse(json, out AppSettings loaded))
            {
                return Normalize(loaded);
            }

            // Genuinely not valid content, which is the one case where moving the
            // file aside is right. It is renamed rather than deleted, so the
            // presets in it can still be recovered by hand.
            AppLog.Error("SETTINGS LOAD", new InvalidDataException(
                "settings.json is not valid JSON; the old file has been kept"));
            Quarantine();
            Quarantined = true;
        }

        // Nothing usable at the real path. The swap writes a copy of the previous
        // good file every time it saves, so there is usually something to fall
        // back to, and it is far better than starting the user off from scratch.
        if (TryRead(BackupPath, out _) is { } backupJson && TryParse(backupJson, out AppSettings recovered))
        {
            AppLog.Warn("SETTINGS recovered from " + Path.GetFileName(BackupPath));
            RecoveredFromBackup = true;
            return Normalize(recovered);
        }

        return Normalize(new AppSettings());
    }

    /// <summary>
    /// Reads a file, retrying a few times, and distinguishes "not there" from
    /// "there and could not be read".
    /// <para>
    /// The sharing mode is the reason this is worth doing at all.
    /// <c>File.ReadAllText</c> opens with <see cref="FileShare.Read"/>, so a second
    /// process reading the same file at the same time is enough to fail, which is
    /// a normal thing on a machine with a cloud sync client and not a sign of
    /// anything wrong with the file.
    /// </para>
    /// </summary>
    private static string? TryRead(string path, out bool unreadable)
    {
        unreadable = false;

        if (!File.Exists(path))
        {
            return null;
        }

        for (int attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("SETTINGS READ " + Path.GetFileName(path)
                    + " attempt " + attempt.ToString(CultureInfo.InvariantCulture)
                    + ": " + ex.GetType().Name);
                if (attempt < ReadAttempts)
                {
                    Thread.Sleep(150 * attempt);
                }
            }
        }

        unreadable = true;
        return null;
    }

    /// <summary>
    /// Parses a profile, reporting a failure rather than throwing. Anything the
    /// caller cannot act on is the same thing to it: no usable profile here.
    /// </summary>
    private static bool TryParse(string json, out AppSettings settings)
    {
        try
        {
            AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(json, Options);
            settings = parsed!;
            return parsed is not null;
        }
        // The variable is in the filter and not in the body, so it is dead. The catch
        // at line 242 is the same shape and does use it, which is what makes this
        // one look like a mistake rather than a copy.
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            settings = null!;
            return false;
        }
    }

    /// <summary>
    /// Moves an unreadable settings file aside under a timestamped name, so the
    /// presets in it are still there to be recovered by hand and the next save
    /// cannot quietly destroy them.
    /// </summary>
    private void Quarantine()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(SettingsPath, SettingsPath + ".corrupt." + stamp + ".json", overwrite: true);
        }
        catch (Exception ex)
        {
            // The file stays where it is and the next save overwrites it, which is
            // the outcome this was meant to prevent. Failing here must not stop
            // the app from starting, so this is logged and nothing more.
            AppLog.Error("SETTINGS QUARANTINE", ex);
            return;
        }

        CapQuarantine();
    }

    /// <summary>
    /// Keeps only the newest few kept-aside files.
    /// <para>
    /// They were only ever meant to be a safety net for one bad launch, and
    /// without a ceiling a machine that launches into a corrupt profile once a
    /// day accumulates a file per day forever. The stamp sorts lexically, so
    /// ordering by name is ordering by time.
    /// </para>
    /// </summary>
    private void CapQuarantine()
    {
        try
        {
            foreach (string old in Directory
                .GetFiles(_folder, "settings.json.corrupt.*.json")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(KeepQuarantineFiles))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("SETTINGS QUARANTINE CAP", ex);
        }
    }

    /// <summary>
    /// Writes the profile, unless the one on disk could not be read at launch.
    /// <para>
    /// That hold is the whole reason the unreadable case is tracked. Starting the
    /// app on defaults and then saving them is harmless until the file turns out
    /// to be perfectly good and simply busy, and by then the original is gone and
    /// nothing in the session can bring it back.
    /// </para>
    /// </summary>
    public void Save(AppSettings settings)
    {
        if (_unreadable)
        {
            AppLog.Warn("SETTINGS SAVE skipped: settings.json could not be read at launch");
            return;
        }

        try
        {
            Directory.CreateDirectory(_folder);

            // Serialised in full first, so a failure here is a failure to
            // serialise rather than a half written file on disk.
            //
            // Under the profile's own gate, because something else writes to it
            // from a worker - the backlight worker adds to the exclusion list and
            // to the remembered brightness - and enumerating a collection while
            // another thread is adding to it throws. Only the serialisation is
            // inside; the disk write is outside, so a slow drive does not hold
            // up everything else that wants to read the profile.
            string json;
            lock (settings.Gate)
            {
                json = JsonSerializer.Serialize(settings, Options);
            }

            // Written beside the real file and swapped in, because Commit runs on
            // nearly every interaction and a plain overwrite leaves a window in
            // which a crash, a forced kill or a lost power cuts the file in half.
            // The result is that the next launch cannot read it, and a truncated
            // settings file takes every preset, slot and key with it.
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, json);
            AtomicSwap(temp, SettingsPath);
        }
        catch (Exception ex)
        {
            // Same reasoning as the load path, and kept under its own tag so a
            // failed save is distinguishable from a failed read in the log.
            AppLog.Error("SETTINGS SAVE", ex);
        }
    }


    /// <summary>
    /// Puts <paramref name="temp"/> where <paramref name="target"/> belongs, in
    /// one step, leaving the previous file alongside it as a backup.
    /// <para>
    /// Replace is the atomic swap on Windows, but it requires the target to exist
    /// already, which on a first run it does not. So the first write is a move and
    /// every write after that is a replace.
    /// </para>
    /// </summary>
    internal static void AtomicSwap(string temp, string target)
    {
        if (File.Exists(target))
        {
            File.Replace(temp, target, target + ".bak", ignoreMetadataErrors: true);
            return;
        }

        File.Move(temp, target);
    }

    /// <summary>
    /// Brings a profile that came from disk, or from a backup file, into a state
    /// the rest of the app can rely on.
    /// <para>
    /// This is a schema guard, not a tidy-up. Everything reaching it is data from
    /// outside the running app: a settings file that a crash truncated, a backup
    /// written by a different build, or a file a user edited by hand. Null lists
    /// and a band array of the wrong length are the easy cases, and they are what
    /// this used to handle. The values themselves were trusted, which meant a
    /// single out-of-range number survived all the way to the gamma ramp or the
    /// engine and produced a white screen or a dead fader with nothing on screen
    /// to explain it. Every numeric field is therefore pulled back inside the
    /// range the app's own controls enforce.
    /// </para>
    /// </summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        // A profile written before the EffectsEnabled rename still carries the old
        // key, and restoring a backup replaces the whole profile without passing
        // through the startup migration. Folding it in here as well means both
        // routes keep whatever state the user saved. Harmless on a profile that has
        // already been through Migrate, which clears the legacy value.
        settings.AdoptLegacyBypass();

        // Same question the engine's own path setter asks, so a profile cannot
        // pass the schema guard with a path that is then thrown away a moment
        // later, and cannot carry one past it either. The path ends up as the
        // FileName of a launched process and reaches here from a settings file or
        // an imported backup, so it is checked rather than trusted.
        if (!AudioService.IsPlausibleFxSoundPath(settings.FxSoundPath))
        {
            settings.FxSoundPath = AudioService.DefaultFxSoundPath;
        }

        // A panic key that names a key nobody can press is worse than none, because
        // the user believes they have one. Same rule the slot bindings get.
        if (!string.IsNullOrWhiteSpace(settings.EmergencyHotkey)
            && !HotkeyService.IsBindable(settings.EmergencyHotkey))
        {
            settings.EmergencyHotkey = string.Empty;
        }


settings.CustomDisplayPresets ??= new List<DisplayPreset>();
        settings.CustomAudioPresets ??= new List<AudioPreset>();
        settings.CustomCombos ??= new List<ComboPreset>();
        settings.AppProfiles ??= new List<AppProfile>();
        settings.Slots ??= new List<HotkeySlot>();

        // The lists themselves being null is the easy half, and it used to be the
        // only half handled. A null *element* is not caught by a null check on
        // the list, and one arriving from a hand-edited or externally produced
        // file went straight through to code that dereferences
        // slot.MonitorDevice.Length or profile.ExePath.Length, which is a crash
        // on a restore with nothing on screen to explain it.
        //
        // Nullability annotations do not help here: they are compile time advice
        // and the deserializer sets whatever the JSON says regardless.
        //
        // Before the migration below, not after. Migrate reads slot.Id on every
        // entry and hands out a fresh one when it is blank, so a null slot has to
        // be gone by the time it runs rather than by the time anything downstream
        // looks at the list.
        settings.CustomDisplayPresets.RemoveAll(p => p is null);
        settings.CustomAudioPresets.RemoveAll(p => p is null);
        settings.CustomCombos.RemoveAll(p => p is null);
        settings.AppProfiles.RemoveAll(p => p is null);
        settings.Slots.RemoveAll(s => s is null);

        // RemoveAll rather than Remove inside the loop, which throws. Removing
        // bumps the list's version and the enumerator checks that on every step,
        // so a single blank entry raised InvalidOperationException out of here -
        // and because the file had already parsed, that escaped Load, escaped the
        // window constructor and landed on the "could not start" box, with the
        // quarantine path never reached and the offending file left exactly where
        // it was. Not recoverable without editing settings.json by hand.
        settings.ExcludedDdcMonitors ??= new List<string>();
        settings.OriginalHardwareBrightness ??= new Dictionary<string, uint>();
        settings.ExcludedDdcMonitors.RemoveAll(string.IsNullOrWhiteSpace);

        // The same goes for the strings inside each entry. A declared string
        // property with a non-null default is only that until a file says
        // otherwise, so every one the rest of the app treats as always present is
        // put back to empty here rather than at each place that reads it.
        settings.OutputDeviceId ??= string.Empty;
        settings.OutputDeviceName ??= string.Empty;
        settings.ActiveDisplayPresetId ??= string.Empty;
        settings.ActiveAudioPresetId ??= string.Empty;
        settings.EmergencyHotkey ??= string.Empty;

        foreach (HotkeySlot slot in settings.Slots)
        {
            slot.Id ??= string.Empty;
            slot.Name ??= string.Empty;
            slot.Hotkey ??= string.Empty;
            slot.MonitorDevice ??= string.Empty;
        }

        foreach (AppProfile profile in settings.AppProfiles)
        {
            profile.Id ??= string.Empty;
            profile.Name ??= string.Empty;
            profile.ExePath ??= string.Empty;
            profile.ProcessName ??= string.Empty;
            profile.DisplayPresetId ??= string.Empty;
            profile.AudioPresetId ??= string.Empty;
            profile.Source ??= string.Empty;
        }

        foreach (ComboPreset combo in settings.CustomCombos)
        {
            combo.Id ??= string.Empty;
            combo.Name ??= string.Empty;
            combo.Tag ??= string.Empty;
            combo.Hotkey ??= string.Empty;
            combo.DisplayPresetId ??= string.Empty;
            combo.AudioPresetId ??= string.Empty;
        }

        foreach (DisplayPreset preset in settings.CustomDisplayPresets)
        {
            preset.Id ??= string.Empty;
            preset.Name ??= string.Empty;
            preset.Tag ??= string.Empty;
        }

foreach (AudioPreset preset in settings.CustomAudioPresets)
        {
            preset.Id ??= string.Empty;
            preset.Name ??= string.Empty;
            preset.Tag ??= string.Empty;
        }

        // Migration goes last of the shape-fixing, not before it. It reads
        // slot.Id, combo.Id and profile fields on every entry, and hands out a
        // fresh id when one is blank - which is exactly the read that threw on a
        // file carrying a null. That guard used to sit 45 lines further down, after
        // the call, so the guard existed and was simply too late: the exception
        // escaped Normalize, escaped Load, escaped the window constructor and
        // landed on the "could not start" box, with the quarantine path never
        // reached because the file had parsed perfectly well. Unrecoverable without
        // editing settings.json by hand.
        settings.Slots = SlotService.Migrate(settings);

        foreach (AppProfile profile in settings.AppProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = AppProfileTools.NewId("app");
            }
        }

        foreach (DisplayPreset preset in settings.CustomDisplayPresets)
        {
            preset.Clamp();
        }

        foreach (AudioPreset preset in settings.CustomAudioPresets)
        {
            int wanted = preset.NumBands <= 0 ? AudioPreset.PresetBandCount : preset.NumBands;
            if (!AudioPreset.BandCounts.Contains(wanted))
            {
                wanted = AudioPreset.PresetBandCount;
            }

            preset.NumBands = wanted;
            if (preset.Bands is null || preset.Bands.Length != wanted)
            {
                double[] fixedBands = new double[wanted];
                if (preset.Bands is not null)
                {
                    for (int i = 0; i < preset.Bands.Length && i < fixedBands.Length; i++)
                    {
                        fixedBands[i] = preset.Bands[i];
                    }
                }

                preset.Bands = fixedBands;
            }

            if (preset.Frequencies is null)
            {
                preset.Frequencies = Array.Empty<double>();
            }
            else if (preset.Frequencies.Length > 0)
            {
                // Sized to the band count first, so Clamp reads a real window for
                // every band rather than a default one for the tail.
                preset.Frequencies = Resized(preset.Frequencies, wanted);
            }

            preset.Clamp();
        }

        return settings;
    }

    /// <summary>Grows or trims a stored array to length, keeping what fits.</summary>
    private static double[] Resized(double[] source, int length)
    {
        if (source.Length == length)
        {
            return source;
        }

        double[] result = new double[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = i < source.Length ? source[i] : 0.0;
        }

        return result;
    }
}

