using System;
using System.Collections.Generic;

namespace GamerTool.Models;

public sealed class AppProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ExePath { get; set; } = string.Empty;

    public string ProcessName { get; set; } = string.Empty;

    public string DisplayPresetId { get; set; } = string.Empty;

    public string AudioPresetId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Source { get; set; } = "APP";

    public AppProfile Copy()
    {
        return new AppProfile
        {
            Id = Id,
            Name = Name,
            ExePath = ExePath,
            ProcessName = ProcessName,
            DisplayPresetId = DisplayPresetId,
            AudioPresetId = AudioPresetId,
            Enabled = Enabled,
            Source = Source
        };
    }
}

public sealed class AppCandidate : System.ComponentModel.INotifyPropertyChanged
{
    private System.Windows.Media.ImageSource? _icon;

    public string Name { get; set; } = string.Empty;

    public string ExePath { get; set; } = string.Empty;

    public string ProcessName { get; set; } = string.Empty;

    public string Source { get; set; } = "APP";

    /// <summary>
    /// Launcher icon for the target. Resolved lazily when the dropdown is opened,
    /// so a long scanned app list never slows startup.
    /// </summary>
    public System.Windows.Media.ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
            {
                return;
            }

            _icon = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public override string ToString()
    {
        return Name;
    }
}

public static class AppProfileTools
{
    public static string NewId(string prefix)
    {
        return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    public static string ProcessNameOf(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return string.Empty;
        }

        string file = exePath;
        int cut = file.LastIndexOf('\\');
        if (cut >= 0)
        {
            file = file.Substring(cut + 1);
        }

        int dot = file.LastIndexOf('.');
        return dot > 0 ? file.Substring(0, dot) : file;
    }
}
