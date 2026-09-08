using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GamerTool.ViewModels;

/// <summary>
/// Zero-dependency INotifyPropertyChanged base - no CommunityToolkit.Mvvm or
/// any other MVVM NuGet package, consistent with GamerTool's
/// zero-external-dependency requirement.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Sets the backing field and raises PropertyChanged only if the value
    /// actually changed. Returns true when it did, so callers can chain
    /// additional side effects (e.g. re-applying a computed gamma ramp) off
    /// the same check without duplicating the equality test.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
