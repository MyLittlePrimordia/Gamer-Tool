using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class AppLibraryService
{
    private static readonly string[] SkipFolders = { "bin", "redist", "_commonredist", "support", "engine", "directx", "dotnet", "installers", "uninstall" };

    /// <summary>
    /// Whole exe file names that are never a game, whatever else they are called.
    /// <para>
    /// Exact rather than partial on purpose. These are the ones where the word is
    /// the whole identity - Unity's crash handler is not a game that happens to
    /// mention Unity, it is Unity's crash handler - and an exact list keeps them
    /// caught without needing "unity" or "mono" as a word of their own, which is
    /// what used to cost Monopoly and Monolith their place in the list.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> JunkExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityCrashHandler32.exe", "UnityCrashHandler64.exe", "UnityCrashHandler.exe",
        "UnityDomainLoad.exe", "UnityPlayer.dll",
        "CrashReportClient.exe", "UnrealCEFSubProcess.exe", "QtWebEngineProcess.exe",
        "dotnet.exe", "BEService.exe", "BEService_x64.exe", "BEService_Background.exe",
        "EasyAntiCheat_Setup.exe", "EasyAntiCheat_EOS_Setup.exe",
        "vc_redist.x64.exe", "vc_redist.x86.exe", "vc_redist.arm64.exe",
        "dxsetup.exe", "vcredist_x64.exe",
    };

    /// <summary>
    /// Words that mean an exe is plumbing, matched as whole words in the file name
    /// and never as fragments of one.
    /// <para>
    /// Whole words is the entire point of this list existing. It used to be a
    /// substring test, which meant <c>CommunityGame.exe</c> was skipped for
    /// containing "unity", <c>Monopoly.exe</c> and <c>Monolith.exe</c> for containing
    /// "mono", and <c>CrashBandicoot.exe</c> for containing "crash". Those games
    /// simply did not appear in the app picker, with nothing on screen to explain
    /// why - and "the list is missing my game" is indistinguishable from the list
    /// being broken.
    /// </para>
    /// <para>
    /// So each entry here is a word that cannot be a game's own name, rather than a
    /// fragment that cannot. "update", "service" and "report" were on the old list
    /// and are deliberately not on this one: Update2.exe and ServicePack.exe are
    /// plausible game names, and neither can be told apart from plumbing by the
    /// word alone. The crash reporter is caught by
    /// <see cref="JunkCrashCompanions"/> instead, which needs both halves.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> JunkWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "unins", "uninstall", "uninstaller", "setup", "installer", "redist", "updater",
        "helper", "cef", "config", "mcp", "dotnet",
    };

    /// <summary>
    /// Words that make "crash" mean a crash reporter rather than a game about
    /// crashing.
    /// <para>
    /// "crash" on its own cannot be filtered, because Crash Bandicoot and its
    /// sequels are real games and the word is the first one in their names. Paired
    /// with any of these it is unambiguous: CrashReportClient, UnityCrashHandler64
    /// and CrashHandler are all plumbing, and none of them can be made to look like
    /// a game by accident.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> JunkCrashCompanions = new(StringComparer.OrdinalIgnoreCase)
    {
        "handler", "reporter", "report", "client", "sender", "dump", "service",
    };

    /// <summary>
    /// Splits an exe name into the words a person would write in it.
    /// <para>
    /// Camel case and digits both break a word, so <c>UnityCrashHandler64</c> comes
    /// out as Unity, Crash, Handler, 64 and <c>unins000</c> as unins, 000. That is
    /// what lets the filter above catch Unity's crash handler on the strength of
    /// "crash" plus "handler" while leaving a game called Monolith alone, because
    /// Monolith is one word and not two.
    /// </para>
    /// <para>
    /// The acronym branch exists for the same reason. Without it, CEFSubProcess
    /// would arrive as one long token and the "cef" entry would never match
    /// anything, because no exe is actually called cefsubprocess.
    /// </para>
    /// </summary>
    private static readonly Regex WordSplitter = new(
        @"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>

    /// Words that mark a registry DisplayName as a maintenance tool rather than a
    /// program the user installed to play.
    /// <para>
    /// This is deliberately not <see cref="JunkWords"/>. Both are matched against
    /// file names, but a DisplayName is a sentence rather than a file name, so a
    /// word anywhere in it is much weaker evidence: the game's own title is the
    /// string most likely to contain "update" or "setup", and this list is not
    /// trying to catch those. Both lists are guesswork and neither is complete;
    /// what this one has to do is catch the utilities that arrive with no helpful
    /// exe name to filter on, such as an installer whose binary is called
    /// AMDRSServ.
    /// </para>
    /// </summary>
    private static readonly string[] UtilityNames =
    {
        "7-zip", "install manager", "driver update", "graphics driver", "updater", "setup",
        "uninstall tool", "windows installer"
    };

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

    /// <summary>
    /// Programs running right now with a window open, for the picker to offer.
    /// <para>
    /// Binding a slot to a game is only as good as the path it was bound to, and a
    /// scan of the registry and the Steam library is guessing at it. This asks the
    /// machine instead, which is the whole reason it exists: a game that is
    /// installed somewhere unusual, or behind a launcher, is found by looking at
    /// the process rather than by knowing where launchers keep their manifests.
    /// </para>
    /// <para>
    /// Not cached, and not part of <see cref="Scan"/>. What is running changes
    /// minute to minute, so this is called when the dropdown opens rather than
    /// folded into the list the scan produces.
    /// </para>
    /// </summary>
    public IReadOnlyList<AppCandidate> ScanRunning()
    {
        List<AppCandidate> found = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        Process[] running;
        try
        {
            running = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            // The whole thing fails or nothing does. No list is better than a list
            // that is quietly missing everything, because the user cannot tell the
            // difference between "no games running" and "this did not work".
            TraceLog.Write("RUNNING", ex);
            return found;
        }

        foreach (Process process in running)
        {
            // In the try, and not only on the happy path. Every Process holds an
            // OS handle, so one that goes out of scope un-disposed leaks a handle
            // for the life of the app - and the scan button being pressed repeatedly
            // makes that quick.
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                // No top level window means not something the user is looking at:
                // a service, a tray helper, a background updater. The window test is
                // what keeps this list to things a person would recognise.
                if (process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                string path = ProcessWatcherService.ReadImagePath((uint)process.Id);
                if (string.IsNullOrWhiteSpace(path) || IsSystemBinary(path))
                {
                    continue;
                }

                string name = process.ProcessName;
                if (ShellProcessNames.Contains(name))
                {
                    continue;
                }

                if (!seen.Add(path))
                {
                    continue;
                }

                // The window title is the useful name when there is one - it is what
                // the user calls the game - and the process name otherwise. Steam
                // games often title the window with the game rather than the exe.
                string title = process.MainWindowTitle?.Trim() ?? string.Empty;

                found.Add(new AppCandidate
                {
                    Name = title.Length > 0 ? title : name,
                    ExePath = path,
                    ProcessName = name,
                    Source = "RUNNING"
                });
            }
            catch (Exception ex)
            {
                // Per process, so one that cannot be read does not lose the rest.
                // Access denied is the ordinary case here rather than an oddity: a
                // game running elevated, or one with anti-cheat, will not hand its
                // path to a process that is not. Those simply do not appear, and
                // there is nothing the user could do about it anyway.
                TraceLog.Write("RUNNING", ex);
            }
            finally
            {
                process.Dispose();
            }
        }

        return found
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Processes that are always running and are never a game.
    /// <para>
    /// The same names <see cref="ProcessWatcherService"/> treats as "not the app
    /// the user is in", listed again rather than shared because that class is
    /// deciding what is in the foreground and this one is deciding what is a game,
    /// and a name can belong in one and not the other.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ShellProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "TextInputHost", "SearchApp", "ShellExperienceBroker", "ApplicationFrameHost",
        "dwm", "SystemSettings", "Taskmgr", "SearchUI", "Widgets",
    };

    public IReadOnlyList<AppCandidate> ScanInstalledPrograms()
    {
        List<AppCandidate> found = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        // The hive is stated next to the path rather than guessed from it. The old
        // code chose between LocalMachine and CurrentUser by testing whether the
        // path began with "SOFTWARE\", which every key here does, so CurrentUser
        // was never read and the third root was a byte-for-byte copy of the
        // first. Per-user installs are now actually looked at.
        (RegistryHive Hive, string Path)[] roots =
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach ((RegistryHive hive, string root) in roots)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
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
                        if (string.IsNullOrWhiteSpace(display) || IsUtilityName(display))
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

    /// <summary>
    /// True for a registry DisplayName that names a maintenance tool rather than
    /// something the user installed to play.
    /// </summary>
    private static bool IsUtilityName(string displayName)
    {
        string lower = displayName.ToLowerInvariant();
        foreach (string word in UtilityNames)
        {
            if (lower.Contains(word, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a path sits inside a directory, separator and all.
    /// <para>
    /// The separator is what stops "C:\Program Files (x86)" from counting as
    /// inside "C:\Program Files", which a plain StartsWith would happily report.
    /// Both sides are compared on their full form first, so a ".." that climbs
    /// out and one that does not are told apart - the comparison here is on the
    /// resolved paths, not on the text that was combined.
    /// </para>
    /// </summary>
    private static bool IsUnder(string path, string root)
    {
        string full = Normalize(Path.GetFullPath(path));
        string under = Normalize(Path.GetFullPath(root));

        if (!full.StartsWith(under, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return full.Length == under.Length || full[under.Length] == '\\';
    }

    private static bool IsSystemBinary(string exe)
    {
        string clean = Normalize(exe);

        string windows = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (windows.Length > 0 && clean.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The Package Cache is where Windows Installer unpacks an MSI so it can
        // repair or patch it later. What lives there is installer payload, not a
        // program anybody launches.
        //
        // The old condition also required the path to be *outside* Program Files,
        // which is never both true at once because the cache is always inside it.
        // So a filter written to keep installers out never once kept one out.
        return clean.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase);
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
                // Every "path" at any depth, not just the top level. Steam wraps
                // each library in a numbered object, so a top-level read returns
                // an empty section and every game on a drive other than the
                // Steam root quietly stops existing.
                foreach (string path in Vdf.ParseAllValues(File.ReadAllText(vdf), "path"))
                {
                    if (!string.IsNullOrWhiteSpace(path))
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

                // Contained, because installdir is a string out of a file on disk rather than
                // anything this app wrote. Path.Combine discards everything before
                // it when handed a rooted value, so "installdir" of "C:\Windows"
                // made folder exactly that, and a relative one walked out with
                // enough "..". The result is only ever shown in a dropdown and used
                // for a process-name match and an icon read, so nothing is executed
                // - but a scan that walks out of the library it was asked about is
                // still a scan that should not have gone there, and the fix is two
                // lines rather than an argument about whether the impact is bounded.
                string commonRoot = Path.GetFullPath(Path.Combine(steamapps, "common"));
                string folder = Path.GetFullPath(Path.Combine(commonRoot, installDir));

                if (!IsUnder(folder, commonRoot) || !Directory.Exists(folder))
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

    /// <remarks>
    /// Internal so the choice can be tested against real folders. This decides what
    /// actually launches when a slot fires, so unlike <see cref="IsJunk"/> - where a
    /// wrong answer is a game missing from a list - a wrong answer here is a slot
    /// bound to the wrong program. The heuristic is "largest exe wins", on the
    /// reasoning that a game is bigger than its own redistributable; the tests pin
    /// down both the cases that holds for and the ones it does not.
    /// </remarks>
    internal static string? PickExecutable(string folder, string? preferredName = null)
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

    /// <summary>
    /// Whether an exe file name is plumbing rather than something to launch a game
    /// with, matched on whole words rather than fragments.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the word splitting can be pinned by tests. This
    /// is the filter that decides which games the user is even offered, and the
    /// version before it matched fragments, so Monopoly, Monolith, CommunityGame and
    /// Crash Bandicoot all vanished from the list with nothing to say why. Getting
    /// this quietly wrong is invisible in a build and obvious to the one person using
    /// the app, which is the worst place for a rule to be wrong.
    /// <para>
    /// Names are matched with or without the extension, because the scan hands over
    /// whatever is on disk and a hand-picked path may not have one.
    /// </para>
    /// </remarks>
    internal static bool IsJunk(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // A file with no extension at all still has to be judged on its words, so
        // both spellings go through rather than returning early on a missing ".exe".
        if (JunkExactNames.Contains(name))
        {
            return true;
        }

        string stem = Path.GetFileNameWithoutExtension(name);

        // The stem can still be the full name for something like "vc_redist.x64",
        // whose extension-less form is the stem of the name minus ".exe".
        if (JunkExactNames.Contains(stem + ".exe"))
        {
            return true;
        }

        if (name.StartsWith("ue4prereq", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool crashed = false;
        foreach (Match word in WordSplitter.Matches(stem))
        {
            if (JunkWords.Contains(word.Value))
            {
                return true;
            }

            // Remembered rather than returned, because "crash" is only junk once a
            // second word turns up. CrashReportClient is plumbing; Crash Bandicoot
            // is a game, and the two differ only in what follows.
            if (string.Equals(word.Value, "crash", StringComparison.OrdinalIgnoreCase))
            {
                crashed = true;
            }
            else if (JunkCrashCompanions.Contains(word.Value))
            {
                // Only counts once something in the name has already said "crash", or
                // "report"/"client"/"service" on their own would take out perfectly
                // ordinary game names.
                return crashed;
            }
        }

        return false;
    }

    private static string Normalize(string path)
    {
        return path.Replace('/', '\\').TrimEnd('\\');
    }
}

public static class Vdf
{
    /// <summary>
    /// Every value filed under a name, at any depth.
    /// <para>
    /// Steam writes two different shapes and they need different parsers. An
    /// <c>appmanifest_*.acf</c> puts everything at the top level, which is what
    /// <see cref="ParseSections"/> reads. A <c>libraryfolders.vdf</c> wraps each
    /// entry in a numbered object - <c>"libraryfolders" { "0" { "path" "D:\\..." } }</c>
    /// - so the paths this app needs most are exactly the ones a top-level reader
    /// steps over.
    /// </para>
    /// <para>
    /// That was not visible, because the manifest parse still succeeded. The scan
    /// simply came back with every game outside the Steam root missing, which
    /// looks like "you do not have those games installed" rather than a parser
    /// that cannot see them.
    /// </para>
    /// <para>
    /// Deliberately not a tree. Nothing here wants one, and building a nested
    /// dictionary for a file read once at startup is the kind of thing that makes
    /// the next reader believe the shape is more complicated than it is.
    /// </para>
    /// </summary>
    public static List<string> ParseAllValues(string text, string name)
    {
        List<string> found = new();
        List<string> tokens = Tokenize(text);
        int index = 0;
        CollectValues(tokens, ref index, name, found, 0);
        return found;
    }

    /// <summary>Walks every token pair, descending into objects rather than over them.</summary>
    private static void CollectValues(List<string> tokens, ref int index, string name, List<string> found, int depth)
    {
        // The same cap the other walk uses, for the same reason: a runaway nest
        // must be a refused parse and not a stack overflow, which cannot be caught
        // and takes the process down with no log line and no emergency reset.
        if (depth > MaxVdfDepth)
        {
            index = tokens.Count;
            return;
        }

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
                CollectValues(tokens, ref index, name, found, depth + 1);
                continue;
            }

            if (string.Equals(token, name, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(Unescape(next));
            }

            index += 2;
        }
    }

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

        return sections;    }

    /// <summary>
    /// How deep a VDF object may nest before the parse gives up.
    /// <para>
    /// Real manifests are two or three deep. Without a cap, a corrupt or
    /// deliberately hostile appmanifest or libraryfolders file nests far enough
    /// to run the stack out, and a stack overflow cannot be caught: the process
    /// dies with no log line and no emergency reset, which for this app means a
    /// gamma ramp left applied to the screen. Refusing to go deeper turns that
    /// into a logged failure and a game missing from the list.
    /// </para>
    /// </summary>
    private const int MaxVdfDepth = 32;

    private static void ParseInto(List<string> tokens, ref int index, Dictionary<string, string> target) =>
        ParseInto(tokens, ref index, target, 0);

    private static void ParseInto(List<string> tokens, ref int index, Dictionary<string, string> target, int depth)
    {
        if (depth > MaxVdfDepth)
        {
            index = tokens.Count;
            return;
        }

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

                // Nested objects were parsed and thrown away, which is all the
                // fields this app reads need, so the walk stops descending here
                // rather than building a tree nobody looks at.
                ParseInto(tokens, ref index, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), depth + 1);
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
