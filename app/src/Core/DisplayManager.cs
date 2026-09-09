using System;
using System.IO;

namespace GamerTool.Core;

/// <summary>
/// Owns all interaction with the physical display gamma ramp: computing curves
/// from user parameters, applying them via GDI32, and guaranteeing the factory
/// ramp is restored no matter how the process exits. This is the single most
/// safety-critical class in GamerTool - the gamma ramp is a global OS resource
/// tied to the display driver, not to this process, so a crash while a custom
/// ramp is applied leaves every application on the machine color-shifted until
/// something resets it.
/// </summary>
public sealed class DisplayManager
{
    private static readonly Lazy<DisplayManager> _instance = new(() => new DisplayManager());
    public static DisplayManager Instance => _instance.Value;

    private readonly object _lock = new();
    private RAMP? _factoryRamp;
    private RAMP? _currentRamp;
    private bool _alreadyRestored;
    private bool _initialized;

    private static readonly string AppDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GamerTool");

    private static readonly string FactoryRampPath = Path.Combine(AppDataFolder, "factory_ramp.bin");
    private static readonly string SessionLockPath = Path.Combine(AppDataFolder, "session.lock");

    private DisplayManager() { }

    /// <summary>
    /// Must be called exactly once at application startup, before any UI reads or
    /// writes gamma sliders. Handles stale-session recovery, captures the
    /// factory ramp, and persists it to disk as a crash-proof fallback.
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(AppDataFolder);

            // Stale-ramp detection: if session.lock exists, the previous run
            // died dirty while a custom ramp was applied. Restore factory gamma
            // from disk before doing anything else, so the user's desktop is
            // never left color-shifted because we crashed last time.
            if (File.Exists(SessionLockPath) && File.Exists(FactoryRampPath))
            {
                try
                {
                    var recovered = LoadRampFromDisk(FactoryRampPath);
                    ApplyRampInternal(recovered);
                }
                catch
                {
                    // Best-effort recovery only; if the saved ramp itself is
                    // corrupt, fall through to capturing a fresh factory ramp
                    // below rather than throwing during startup.
                }
                finally
                {
                    TryDeleteFile(SessionLockPath);
                }
            }

            // Capture the current (now-guaranteed-factory) ramp from the live
            // display and persist it both in memory and to disk.
            var current = ReadCurrentRampFromDevice();
            _factoryRamp = current.Clone();
            _currentRamp = current.Clone();
            SaveRampToDisk(_factoryRamp.Value, FactoryRampPath);

            _initialized = true;
        }
    }

    /// <summary>
    /// Computes a full 3x256 gamma ramp from the documented display math:
    ///   Normalized       = i / 255
    ///   GammaAdjusted     = Normalized ^ (1 / Gamma)
    ///   ContrastAdjusted  = (GammaAdjusted - 0.5) * Contrast + 0.5
    ///   Lifted            = ContrastAdjusted + ShadowLift * (1 - Normalized)^2 + BrightnessOffset
    ///   Value             = Clamp(Lifted * ChannelGain * 65535, 0, 65535)
    /// The clamp is applied exactly once, after the entire multiply chain per
    /// channel per index - never per intermediate term, or highlights band
    /// incorrectly at high ChannelGain/BrightnessOffset combinations.
    /// </summary>
    public RAMP ComputeRamp(
        double gamma,
        double contrast,
        double shadowLift,
        double brightnessOffset,
        double gainRed,
        double gainGreen,
        double gainBlue,
        bool enforceMonotonic = true)
    {
        // Defensive clamp on input even though the UI slider ranges already
        // enforce these bounds - Gamma in particular must never reach 0
        // (division by zero in the exponent).
        gamma = Math.Clamp(gamma, 0.5, 3.0);
        contrast = Math.Clamp(contrast, 0.5, 2.0);
        shadowLift = Math.Clamp(shadowLift, 0.0, 0.5);
        brightnessOffset = Math.Clamp(brightnessOffset, -0.25, 0.25);
        gainRed = Math.Clamp(gainRed, 0.0, 1.5);
        gainGreen = Math.Clamp(gainGreen, 0.0, 1.5);
        gainBlue = Math.Clamp(gainBlue, 0.0, 1.5);

        var ramp = new RAMP
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };

        ComputeChannel(ramp.Red, gamma, contrast, shadowLift, brightnessOffset, gainRed, enforceMonotonic);
        ComputeChannel(ramp.Green, gamma, contrast, shadowLift, brightnessOffset, gainGreen, enforceMonotonic);
        ComputeChannel(ramp.Blue, gamma, contrast, shadowLift, brightnessOffset, gainBlue, enforceMonotonic);

        return ramp;
    }

    private static void ComputeChannel(
        ushort[] channel,
        double gamma,
        double contrast,
        double shadowLift,
        double brightnessOffset,
        double channelGain,
        bool enforceMonotonic)
    {
        double previous = 0.0;

        for (int i = 0; i < 256; i++)
        {
            double normalized = i / 255.0;
            double gammaAdjusted = Math.Pow(normalized, 1.0 / gamma);
            double contrastAdjusted = (gammaAdjusted - 0.5) * contrast + 0.5;
            double shadowTerm = shadowLift * Math.Pow(1.0 - normalized, 2.0);
            double lifted = contrastAdjusted + shadowTerm + brightnessOffset;

            // Single clamp point, applied after the full multiply chain.
            double value = Math.Clamp(lifted * channelGain * 65535.0, 0.0, 65535.0);

            if (enforceMonotonic && value < previous)
            {
                // Running-max safety pass: a very high ShadowLift combined with
                // steep Contrast can theoretically produce a dip in the curve.
                // That never crashes anything, but it is a visible
                // banding/inversion artifact, so clamp to the previous (higher)
                // value instead of letting the curve fold backward.
                value = previous;
            }

            channel[i] = (ushort)Math.Round(value);
            previous = value;
        }
    }

    /// <summary>
    /// Applies a computed ramp to the physical display and records it as the
    /// current ramp. Writes the session.lock marker first, so that if the
    /// process dies between the write and the apply completing, the next
    /// launch still knows to restore factory gamma.
    /// </summary>
    public void ApplyRamp(RAMP ramp)
    {
        lock (_lock)
        {
            if (!_initialized)
                throw new InvalidOperationException("DisplayManager.Initialize() must be called before ApplyRamp().");

            WriteSessionLock();
            ApplyRampInternal(ramp);
            _currentRamp = ramp.Clone();
            _alreadyRestored = false;
        }
    }

    /// <summary>
    /// Restores the factory gamma ramp captured at startup. Safe to call
    /// multiple times and from multiple threads/exit hooks concurrently -
    /// guarded by both a lock and an idempotency flag, since Windows happily
    /// accepts redundant SetDeviceGammaRamp calls but overlapping hook races
    /// should never be relied on to behave a particular way.
    /// </summary>
    public void RestoreFactoryGamma()
    {
        lock (_lock)
        {
            if (_alreadyRestored || _factoryRamp is null)
                return;

            try
            {
                ApplyRampInternal(_factoryRamp.Value);
            }
            finally
            {
                _alreadyRestored = true;
                TryDeleteFile(SessionLockPath);
            }
        }
    }

    public RAMP? GetFactoryRamp()
    {
        lock (_lock) { return _factoryRamp?.Clone(); }
    }

    public RAMP? GetCurrentRamp()
    {
        lock (_lock) { return _currentRamp?.Clone(); }
    }

    private static RAMP ReadCurrentRampFromDevice()
    {
        IntPtr hdc = User32Native.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            throw new InvalidOperationException("Unable to acquire a device context for the primary display.");

        try
        {
            var ramp = RAMP.CreateIdentity();
            bool ok = Gdi32Native.GetDeviceGammaRamp(hdc, ref ramp);
            if (!ok)
                throw new InvalidOperationException("GetDeviceGammaRamp failed - the display driver may not support gamma ramps.");
            return ramp;
        }
        finally
        {
            User32Native.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static void ApplyRampInternal(RAMP ramp)
    {
        IntPtr hdc = User32Native.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return; // Nothing we can do without a DC - fail silently rather than throw from an exit hook.

        try
        {
            Gdi32Native.SetDeviceGammaRamp(hdc, ref ramp);
        }
        finally
        {
            User32Native.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private void WriteSessionLock()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(SessionLockPath, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Non-fatal: if we can't write the lock file, ramp application
            // still proceeds. Worst case, a crash during this specific session
            // won't be auto-recovered on next launch - but every in-process
            // exit hook (ProcessExit, UnhandledException, etc.) is unaffected.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Non-fatal cleanup failure.
        }
    }

    private static void SaveRampToDisk(RAMP ramp, string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            WriteChannel(writer, ramp.Red);
            WriteChannel(writer, ramp.Green);
            WriteChannel(writer, ramp.Blue);
        }
        catch
        {
            // Non-fatal: the in-memory factory ramp is still valid for this
            // session. Disk persistence only matters for crash recovery on the
            // *next* launch.
        }
    }

    private static void WriteChannel(BinaryWriter writer, ushort[] channel)
    {
        for (int i = 0; i < 256; i++)
            writer.Write(channel[i]);
    }

    private static RAMP LoadRampFromDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream);

        return new RAMP
        {
            Red = ReadChannel(reader),
            Green = ReadChannel(reader),
            Blue = ReadChannel(reader)
        };
    }

    private static ushort[] ReadChannel(BinaryReader reader)
    {
        var channel = new ushort[256];
        for (int i = 0; i < 256; i++)
            channel[i] = reader.ReadUInt16();
        return channel;
    }
}
