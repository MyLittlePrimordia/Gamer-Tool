using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Win32;

namespace GamerTool.Services;

public static class MonitorNameResolver
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string? deviceId, string? reportedName, int ordinal)
    {
        string fallback = IsUseful(reportedName) ? reportedName!.Trim() : "Screen " + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return fallback;
        }

        string key = deviceId;
        if (Cache.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        string resolved = FromRegistry(deviceId) ?? FromEdid(deviceId) ?? fallback;
        Cache[key] = resolved;
        return resolved;
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
                object? raw = entry?.GetValue("EDID");
                if (raw is not byte[] edid || edid.Length < 128)
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
