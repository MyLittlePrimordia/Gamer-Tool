using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GamerTool.Services;
using Application = System.Windows.Application;

namespace GamerTool;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        foreach (string arg in e.Args)
        {
            if (arg.Equals("--makeicon", StringComparison.OrdinalIgnoreCase))
            {
                string target = GetArg(e.Args, "--makeicon") ?? "GamerTool.ico";
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = "GamerTool.ico";
                }

                try
                {
                    IconFactory.WriteToFile(target);
                    string preview = Path.ChangeExtension(target, ".preview.png");
                    IconFactory.WritePreview(preview, 256);
                    TraceLog.Write("ICON WROTE " + Path.GetFullPath(target) + " " + new FileInfo(target).Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    TraceLog.Write("ICON", ex);
                }

                Shutdown(0);
                return;
            }

            if (arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
            {
                RunSelfTest();
                Shutdown(0);
                return;
            }
        }

        _mutex = new Mutex(true, "GamerToolSingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("GAMER TOOL IS ALREADY RUNNING", "GAMER TOOL", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Opened before anything else can fail, so the first line in the file is
        // the one that explains a start that never got any further.
        AppLog.Info("start " + (typeof(App).Assembly.GetName().Version?.ToString() ?? "?")
            + " on " + Environment.OSVersion.VersionString
            + " " + (Environment.Is64BitProcess ? "x64" : "x86")
            + " args=" + SanitiseArgs(string.Join(' ', e.Args)));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyReset.Run();
        System.Windows.Application.Current.Exit += (_, _) => EmergencyReset.Run();

        base.OnStartup(e);

        try
        {
            MainWindow window = new();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteLog("STARTUP", ex);
            MessageBox.Show("GAMER TOOL COULD NOT START: " + ex.Message, "GAMER TOOL", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog("UI", e.Exception);
        e.Handled = true;
        MessageBox.Show("SOMETHING WENT WRONG: " + e.Exception.Message, "GAMER TOOL", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteLog("APP", ex);
            MessageBox.Show("SOMETHING WENT WRONG: " + ex.Message, "GAMER TOOL", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void RunSlotSelfTest()
    {
        try
        {
            GamerTool.Models.AppSettings sample = new();
            sample.AppProfiles.Add(new GamerTool.Models.AppProfile
            {
                Id = "app_old1",
                Name = "Counter-Strike 2",
                ExePath = @"C:\Games\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe",
                ProcessName = "cs2",
                DisplayPresetId = "camper",
                AudioPresetId = "footstep",
                Enabled = true,
                Source = "STEAM"
            });
            sample.AppProfiles.Add(new GamerTool.Models.AppProfile
            {
                Id = "app_old2",
                Name = "Apex Legends",
                ExePath = @"D:\SteamLibrary\steamapps\common\Apex\Apex.exe",
                ProcessName = "Apex",
                DisplayPresetId = "sniper",
                AudioPresetId = null!,
                Enabled = true,
                Source = "STEAM"
            });

            List<GamerTool.Models.HotkeySlot> slots = SlotService.Migrate(sample);
            TraceLog.Write("SLOTS " + slots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (GamerTool.Models.HotkeySlot slot in slots)
            {
                TraceLog.Write("  SLOT  " + slot.Name
                    + " | key=" + (string.IsNullOrWhiteSpace(slot.Hotkey) ? "none" : slot.Hotkey)
                    + " | screen=" + (slot.DisplayPresetId ?? "none")
                    + " | sound=" + (slot.AudioPresetId ?? "none")
                    + " | auto=" + slot.AutoActivate
                    + " | target=" + slot.TargetText
                    + " | " + slot.WorkText);
            }

            GamerTool.Models.HotkeySlot? byPath = SlotService.MatchForeground(
                slots,
                @"C:\Games\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe",
                "cs2");
            TraceLog.Write("MATCH BY PATH: " + (byPath?.Name ?? "none"));

            GamerTool.Models.HotkeySlot? byName = SlotService.MatchProcessName(slots, "Apex");
            TraceLog.Write("MATCH BY NAME: " + (byName?.Name ?? "none"));

            GamerTool.Models.HotkeySlot? miss = SlotService.MatchProcessName(slots, "chrome");
            TraceLog.Write("MATCH MISS: " + (miss?.Name ?? "none"));

            HashSet<string> names = SlotService.TargetProcessNames(slots);
            TraceLog.Write("WATCH NAMES: " + string.Join(",", names));
        }
        catch (Exception ex)
        {
            TraceLog.Write("SLOTS", ex);
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void RunSelfTest()
    {
        TraceLog.Write("SELFTEST START");
        try
        {
            AppLibraryService library = new();
            IReadOnlyList<string> roots = library.SteamRoots();
            TraceLog.Write("STEAM ROOTS " + roots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (string root in roots)
            {
                foreach (string lib in library.SteamLibraries(root))
                {
                    TraceLog.Write("LIBRARY " + lib);
                }
            }

            IReadOnlyList<GamerTool.Models.AppCandidate> steam = library.ScanSteam();
            TraceLog.Write("STEAM GAMES " + steam.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (GamerTool.Models.AppCandidate candidate in steam)
            {
                TraceLog.Write("  STEAM  " + candidate.Name + " -> " + candidate.ExePath);
            }

            IReadOnlyList<GamerTool.Models.AppCandidate> programs = library.ScanInstalledPrograms();
            TraceLog.Write("PROGRAMS " + programs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (GamerTool.Models.AppCandidate candidate in programs)
            {
                TraceLog.Write("  APP    " + candidate.Name + " -> " + candidate.ExePath);
            }

            AudioService audio = new();
            TraceLog.Write("FXSOUND INSTALLED " + audio.IsInstalled.ToString() + " RUNNING " + audio.IsRunning.ToString());

            RunSlotSelfTest();
            TraceLog.Write("SELFTEST DONE");
        }
        catch (Exception ex)
        {
            TraceLog.Write("SELFTEST", ex);
        }
    }

    /// <summary>
    /// The crash handler's way out. Goes to the same rolling log as everything
    /// else so there is one file to look at, and writes synchronously, because
    /// this runs on the way down and a buffered line would be lost.
    /// </summary>
    public static void WriteLog(string tag, Exception ex)
    {
        AppLog.Error(tag, ex);
    }


    /// <summary>Command line arguments, with the user profile taken out of them.</summary>
    private static string SanitiseArgs(string args) => AppLog.Sanitise(args);

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
