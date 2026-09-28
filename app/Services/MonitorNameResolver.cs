using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace GamerTool.Services;

public static class MonitorNameResolver
{
    /// <summary>
    /// Display names already worked out, keyed by device id.
    /// <para>
    /// Concurrent because <see cref="Resolve"/> is public and the cache is
    /// static. It happened to be called only from the UI thread, but nothing
    /// about the type said so, and a plain dictionary written from two threads
    /// does not throw, it corrupts. Bounded in practice by the number of display
    /// connections, and a fallback name is memoised like any other so a monitor
    /// is not re-read on every scan.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string? deviceId, string? reportedName, int ordinal)
    {
        string fallback = IsUseful(reportedName) ? reportedName!.Trim() : "Screen " + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return fallback;
        }

        return Cache.GetOrAdd(deviceId, key => FromRegistry(key) ?? FromEdid(key) ?? fallback);
    }

    private static bool IsUseful(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string lower = name.Trim().ToLowerInvariant();
        return !lower.Contains("generic pnp monitor")
            && !lower.Contains("generic monitor")
            && !lower.Contains("microsoft basic display")
            && !lower.Equals("display", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FromRegistry(string deviceId)
    {
        try
        {
            int split = deviceId.IndexOf('\\');
            if (split < 0)
            {
                return null;
            }

            string hardwareId = deviceId.Substring(split + 1);
            int cut = hardwareId.IndexOf('{');
            if (cut > 0)
            {
                hardwareId = hardwareId.Substring(0, cut);
            }

            string root = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + hardwareId;
            using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(root);
            if (baseKey is null)
            {
                return null;
            }

            foreach (string sub in baseKey.GetSubKeyNames())
            {
                using RegistryKey? entry = baseKey.OpenSubKey(sub);
                string? friendly = entry?.GetValue("FriendlyName") as string;
                string? model = Extract(friendly);
                if (model is null)
                {
                    model = Extract(entry?.GetValue("DeviceDesc") as string);
                }

                if (model is not null)
                {
                    return model;
                }
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("MONNAME", ex);
        }

        return null;
    }

    private static string? Extract(string? friendly)
    {
        if (string.IsNullOrWhiteSpace(friendly))
        {
            return null;
        }

        string text = friendly;
        int lastParen = text.LastIndexOf('(');
        int closeParen = text.LastIndexOf(')');
        if (lastParen >= 0 && closeParen > lastParen)
        {
            string inner = text.Substring(lastParen + 1, closeParen - lastParen - 1).Trim();
            if (inner.Length > 1 && !inner.Contains("%"))
            {
                return inner;
            }
        }

        int semicolon = text.LastIndexOf(';');
        if (semicolon >= 0 && semicolon < text.Length - 1)
        {
            string tail = text.Substring(semicolon + 1).Trim();
            if (tail.Length > 1 && !tail.StartsWith("@", StringComparison.Ordinal))
            {
                return tail;
            }
        }

        return null;
    }

    private static string? FromEdid(string deviceId)
    {
        try
        {
            int split = deviceId.IndexOf('\\');
            if (split < 0)
            {
                return null;
            }

            string hardwareId = deviceId.Substring(split + 1);
            int cut = hardwareId.IndexOf('{');
            if (cut > 0)
            {
                hardwareId = hardwareId.Substring(0, cut);
            }

            using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + hardwareId);
            if (baseKey is null)
            {
                return null;
            }

            foreach (string sub in baseKey.GetSubKeyNames())
            {
                using RegistryKey? entry = baseKey.OpenSubKey(sub + @"\Device Parameters");
                byte[]? edid = ReadEdidValue(entry?.GetValue("EDID"));

                if (edid is null)
                {
                    continue;
                }

                string? name = ReadDescriptor(edid, 0xFC) ?? ReadDescriptor(edid, 0xFF);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("EDID", ex);
        }

        return null;
    }

    /// <summary>
    /// The EDID as bytes, whichever shape the registry hands it back.
    /// <para>
    /// The value reads back as <c>byte[]</c> in theory and as <c>Object[]</c> in
    /// practice, and a bare <c>is byte[]</c> test throws the second shape away
    /// silently, which is what left this file naming a perfectly good panel
    /// "Screen 1" while the file that reads the same value for the backlight
    /// probe had already worked round it.
    /// </para>
    /// </summary>
    private static byte[]? ReadEdidValue(object? raw)
    {
        return raw switch
        {
            byte[] direct when direct.Length >= 128 => direct,
            object[] boxed when boxed.Length >= 128 => boxed
                .Select(v => v is byte b ? b : (byte)0)
                .ToArray(),
            _ => null
        };
    }

    private static string? ReadDescriptor(byte[] edid, byte tag)
    {
        for (int index = 0; index < 4; index++)
        {
            int offset = 54 + (index * 18);
            if (offset + 18 > edid.Length)
            {
                break;
            }

            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != tag)
            {
                continue;
            }

            StringBuilder text = new();
            for (int i = 5; i < 18; i += 2)
            {
                text.Append((char)edid[offset + i]);
                text.Append((char)edid[offset + i + 1]);
            }

            string value = text.ToString().Trim();
            if (value.Length > 1)
            {
                return value;
            }
        }

        return null;
    }
}
