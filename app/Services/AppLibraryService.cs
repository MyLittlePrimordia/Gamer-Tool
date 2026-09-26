using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class AppLibraryService
{
    private static readonly string[] SkipFolders = { "bin", "redist", "_commonredist", "support", "engine", "directx", "dotnet", "installers", "uninstall" };

    private static readonly string[] SkipWords =
    {
        "unins", "unity", "crash", "mono", "cef", "updater", "update", "service", "mcp", "setup", "config",
        "crashhandler", "report", "vcredist", "redist", "installer", "helper", "launcher", "dotnet", "uninstall"
    };

    private static readonly string[] SkipExtensions = { ".exe" };

    public IReadOnlyList<AppCandidate> Scan(bool includePrograms = true, bool includeSteam = true)
    {
        List<AppCandidate> all = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        if (includeSteam)
        {
            foreach (AppCandidate candidate in ScanSteam())
            {
                if (seen.Add(candidate.ExePath))
                {
                    all.Add(candidate);
                }
            }
        }

        if (includePrograms)
        {
            foreach (AppCandidate candidate in ScanInstalledPrograms())
            {
                if (seen.Add(candidate.ExePath))
                {
                    all.Add(candidate);
                }
            }
        }

        return all;
    }

    public IReadOnlyList<AppCandidate> ScanInstalledPrograms()
    {
        List<AppCandidate> found = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (string root in roots)
        {
            try
            {
                using RegistryKey baseKey = root.StartsWith(@"SOFTWARE\", StringComparison.Ordinal)
                    ? Registry.LocalMachine
                    : Registry.CurrentUser;
                using RegistryKey? key = baseKey.OpenSubKey(root);
                if (key is null)
                {
                    continue;
                }

                foreach (string name in key.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? entry = key.OpenSubKey(name);
                        if (entry is null)
                        {
                            continue;
                        }

                        string? display = entry.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(display))
                        {
                            continue;
                        }

                        string? location = entry.GetValue("InstallLocation") as string;
                        string? icon = entry.GetValue("DisplayIcon") as string;
                        string? exe = ResolveExe(location, icon);
                        if (string.IsNullOrWhiteSpace(exe) || IsSystemBinary(exe) || seen.Add(exe) == false)
                        {
                            continue;
                        }

                        found.Add(new AppCandidate
                        {
                            Name = display.Trim(),
                            ExePath = exe,
                            ProcessName = AppProfileTools.ProcessNameOf(exe),
                            Source = "APP"
                        });
                    }
                    catch (Exception ex)
                    {
                        TraceLog.Write("REG", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                TraceLog.Write("REG", ex);
            }
        }

        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    private static bool IsSystemBinary(string exe)
    {
        string windows = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string clean = Normalize(exe);
        if (clean.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return programFiles.Length > 0 && clean.StartsWith(Normalize(programFiles), StringComparison.OrdinalIgnoreCase) is false
            && clean.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveExe(string? location, string? icon)
    {
        if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
        {
            string? best = PickExecutable(location);
            if (best is not null)
            {
                return best;
            }
        }

        if (!string.IsNullOrWhiteSpace(icon))
        {
            string cleaned = icon.Trim();
            int comma = cleaned.LastIndexOf(',');
            if (comma > 0 && int.TryParse(cleaned.Substring(comma + 1).Trim(), out _))
            {
                cleaned = cleaned.Substring(0, comma);
            }

            cleaned = cleaned.Trim('"');
            if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(cleaned)
                && !IsJunk(Path.GetFileName(cleaned)))
            {
                return cleaned;
            }

            string? folder = Path.GetDirectoryName(cleaned);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                return PickExecutable(folder);
            }
        }

        return null;
    }

    public IReadOnlyList<AppCandidate> ScanSteam()
    {
        List<AppCandidate> found = new();
        foreach (string steam in SteamRoots())
        {
            foreach (string library in SteamLibraries(steam))
            {
                found.AddRange(ScanSteamLibrary(library));
            }
        }

        return found;
    }

    public IReadOnlyList<string> SteamRoots()
    {
        List<string> roots = new();
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            string? path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                AddLibrary(roots, path);
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("STEAM", ex);
        }

        foreach (string guess in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Steam",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Steam"
        })
        {
            AddLibrary(roots, guess);
        }

        return roots;
    }

    public IReadOnlyList<string> SteamLibraries(string steamPath)
    {
        List<string> libraries = new();
        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                Dictionary<string, Dictionary<string, string>> sections = Vdf.ParseSections(File.ReadAllText(vdf));
                foreach (Dictionary<string, string> entry in sections.Values)
                {
                    if (entry.TryGetValue("path", out string? path) && !string.IsNullOrWhiteSpace(path))
                    {
                        AddLibrary(libraries, path);
                    }
                }
            }
            catch (Exception ex)
            {
                TraceLog.Write("VDF", ex);
            }
        }

        AddLibrary(libraries, steamPath);
        libraries.Reverse();
        return libraries;
    }

    private static void AddLibrary(List<string> libraries, string path)
    {
        string clean = Normalize(path);
        if (string.IsNullOrWhiteSpace(clean) || !Directory.Exists(clean))
        {
            return;
        }

        foreach (string existing in libraries)
        {
            if (string.Equals(existing, clean, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        libraries.Add(clean);
    }

    private IEnumerable<AppCandidate> ScanSteamLibrary(string library)
    {
        List<AppCandidate> found = new();
        string steamapps = Path.Combine(library, "steamapps");
        if (!Directory.Exists(steamapps))
        {
            return found;
        }

        string[] manifests;
        try
        {
            manifests = Directory.GetFiles(steamapps, "appmanifest_*.acf");
        }
        catch (Exception ex)
        {
            TraceLog.Write("STEAM", ex);
            return found;
        }

        foreach (string manifest in manifests)
        {
            try
            {
                Dictionary<string, Dictionary<string, string>> sections = Vdf.ParseSections(File.ReadAllText(manifest));
                if (!sections.TryGetValue("AppState", out Dictionary<string, string>? state))
                {
                    continue;
                }

                state.TryGetValue("name", out string? name);
                state.TryGetValue("installdir", out string? installDir);
                if (string.IsNullOrWhiteSpace(installDir))
                {
                    continue;
                }

                string folder = Path.Combine(steamapps, "common", installDir);
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                string? exe = PickExecutable(folder, installDir);
                if (string.IsNullOrWhiteSpace(exe))
                {
                    continue;
                }

                found.Add(new AppCandidate
                {
                    Name = string.IsNullOrWhiteSpace(name) ? installDir : name,
                    ExePath = exe,
                    ProcessName = AppProfileTools.ProcessNameOf(exe),
                    Source = "STEAM"
                });
            }
            catch (Exception ex)
            {
                TraceLog.Write("STEAM", ex);
            }
        }

        return found;
    }

    private static string? PickExecutable(string folder, string? preferredName = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                string direct = Path.Combine(folder, preferredName + ".exe");
                if (File.Exists(direct) && !IsJunk(Path.GetFileName(direct)))
                {
                    return direct;
                }
            }

            FileInfo? best = null;
            foreach (string file in Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly))
            {
                FileInfo info = new(file);
                if (IsJunk(info.Name))
                {
                    continue;
                }

                if (best is null || info.Length > best.Length)
                {
                    best = info;
                }
            }

            if (best is not null)
            {
                return best.FullName;
            }

            foreach (string sub in Directory.EnumerateDirectories(folder))
            {
                string leaf = Path.GetFileName(sub);
                if (SkipFolders.Contains(leaf, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(sub, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    FileInfo info = new(file);
                    if (IsJunk(info.Name))
                    {
                        continue;
                    }

                    if (best is null || info.Length > best.Length)
                    {
                        best = info;
                    }
                }
            }

            return best?.FullName;
        }
        catch (Exception ex)
        {
            TraceLog.Write("EXE", ex);
            return null;
        }
    }

    private static bool IsJunk(string name)
    {
        string lower = name.ToLowerInvariant();
        foreach (string word in SkipWords)
        {
            if (lower.Contains(word, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return lower.StartsWith("ue4prereq", StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        return path.Replace('/', '\\').TrimEnd('\\');
    }
}

public static class Vdf
{
    public static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        List<string> tokens = Tokenize(text);
        int index = 0;
        Dictionary<string, string> root = new(StringComparer.OrdinalIgnoreCase);
        ParseInto(tokens, ref index, root);
        return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase) { { "root", root } };
    }

    public static Dictionary<string, Dictionary<string, string>> ParseSections(string text)
    {
        List<string> tokens = Tokenize(text);
        Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;

        while (index < tokens.Count)
        {
            if (tokens[index].Equals("{", StringComparison.Ordinal) || tokens[index].Equals("}", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            string name = tokens[index];
            index++;
            if (index < tokens.Count && tokens[index].Equals("{", StringComparison.Ordinal))
            {
                index++;
                Dictionary<string, string> body = new(StringComparer.OrdinalIgnoreCase);
                ParseInto(tokens, ref index, body);
                sections[name] = body;
                continue;
            }

            if (index + 1 < tokens.Count)
            {
                sections[name] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "value", tokens[index + 1] } };
                index += 2;
            }
        }

        return sections;
    }

    private static void ParseInto(List<string> tokens, ref int index, Dictionary<string, string> target)
    {
        while (index < tokens.Count)
        {
            string token = tokens[index];

            if (token.Equals("}", StringComparison.Ordinal))
            {
                index++;
                return;
            }

            if (token.Equals("{", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            if (index + 1 >= tokens.Count)
            {
                return;
            }

            string next = tokens[index + 1];
            if (next.Equals("{", StringComparison.Ordinal))
            {
                index += 2;
                ParseInto(tokens, ref index, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                continue;
            }

            target[token] = next;
            index += 2;
        }
    }

    private static string Unescape(string value)
    {
        if (!value.Contains("\\\\", StringComparison.Ordinal))
        {
            return value;
        }

        return value.Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static List<string> Tokenize(string text)
    {
        List<string> tokens = new();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0)
                {
                    break;
                }

                tokens.Add(Unescape(text.Substring(i + 1, end - i - 1)));
                i = end + 1;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
                continue;
            }

            if (c == '{' || c == '}')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '"' && text[i] != '{' && text[i] != '}')
            {
                i++;
            }

            if (i > start)
            {
                tokens.Add(text.Substring(start, i - start));
            }
        }

        return tokens;
    }
}
