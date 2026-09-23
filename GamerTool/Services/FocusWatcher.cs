using System.Windows.Threading;

namespace GamerTool.Services;

/// <summary>
/// Older fullscreen-exclusive DirectX games take ownership of the display and
/// reset the gamma ramp to identity when they gain the screen (and sometimes
/// on every scene change). Newer borderless/DirectFlip games composited by DWM
/// don't have this problem. Rather than hook every game, we just poll on a
/// timer and cheaply re-apply the last preset if it drifted. This is the same
/// pragmatic workaround tools like f.lux and SunsetScreen use.
/// </summary>
public class FocusWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _enabled = true;

    public FocusWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => ReassertIfNeeded();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value) _timer.Start(); else _timer.Stop();
        }
    }

    private void ReassertIfNeeded()
    {
        if (DisplayService.LastApplied is { } preset)
        {
            // Cheap idempotent re-apply; SetDeviceGammaRamp is fast (<1ms) so a
            // 2s poll has no perceptible cost and no visible flicker.
            try { DisplayService.Apply(preset); } catch { /* driver hiccup, retry next tick */ }
        }
    }

    public void Dispose() => _timer.Stop();
}
