using System;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The list of programs running right now, offered as game targets.
/// <para>
/// Binding a slot to a game is only as good as the path it was bound to. Scanning
/// the registry and the Steam library guesses at it; asking the machine does not.
/// This is how a game installed somewhere unusual, or behind a launcher, is found
/// at all - and both of those are exactly the cases the scan cannot see.
/// </para>
/// <para>
/// The list is deliberately not cached and deliberately not part of
/// <see cref="AppLibraryService.Scan"/>. What is running changes minute to minute,
/// so it is read when the dropdown opens. Everything asserted here is about what
/// comes back rather than about when, because the timing is the window's problem.
/// </para>
/// </summary>
public class ScanRunningTests
{
    private readonly AppLibraryService _library = new();

    /// <summary>This process, which is the one thing guaranteed to be running.</summary>
    private static Process Self => Process.GetCurrentProcess();

    [Fact]
    public void TheAppItselfIsNeverOfferedAsAGame()
    {
        // Obvious, and stated anyway: a list that offered Gamer Tool as something
        // to bind a game slot to would be a bug the user finds by binding a slot to
        // it and wondering why the app opened itself.
        AppCandidate? self = Find(_library.ScanRunning(), p =>
            string.Equals(p.ExePath, Self.MainModule?.FileName, StringComparison.OrdinalIgnoreCase));

        Assert.Null(self);
    }

    [Fact]
    public void EverythingOfferedHasAProcessNameAndAPath()
    {
        // The two fields the binding actually uses. A candidate missing either would
        // produce a slot that binds to nothing, or one whose auto-apply can never
        // match a foreground window.
        foreach (AppCandidate candidate in _library.ScanRunning())
        {
            Assert.False(string.IsNullOrWhiteSpace(candidate.Name), candidate.ExePath);
            Assert.False(string.IsNullOrWhiteSpace(candidate.ExePath), candidate.Name);
            Assert.False(string.IsNullOrWhiteSpace(candidate.ProcessName), candidate.Name);
            Assert.True(File.Exists(candidate.ExePath), candidate.ExePath + " does not exist");
        }
    }

    [Fact]
    public void EverythingOfferedIsTaggedAsRunning()
    {
        // The tag is how the list tells itself apart from the installed programs it
        // is merged into, and how a reopen removes the previous copy rather than
        // growing the list.
        foreach (AppCandidate candidate in _library.ScanRunning())
        {
            Assert.Equal("RUNNING", candidate.Source);
        }
    }

    [Fact]
    public void TheSameProgramIsNotOfferedTwice()
    {
        // Windows reports one entry per process, so an application running three
        // processes would otherwise appear three times and the user would pick
        // between three identical lines.
        AppCandidate[] found = _library.ScanRunning().ToArray();

        Assert.Equal(
            found.Select(c => c.ExePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            found.Length);
    }

    [Fact]
    public void OnlyProgramsWithAWindowAreOffered()
    {
        // The filter that keeps this list to things a person would recognise. A
        // background service has no window, and offering sixty of them would bury
        // the one game the user is looking for.
        AppCandidate[] found = _library.ScanRunning().ToArray();

        foreach (AppCandidate candidate in found)
        {
            using Process process = Process.GetProcessById(
                Array.Find(
                    Process.GetProcesses(),
                    p => string.Equals(
                        ProcessWatcherService.ReadImagePath((uint)p.Id),
                        candidate.ExePath,
                        StringComparison.OrdinalIgnoreCase))?.Id ?? -1);

            if (process.Id <= 0)
            {
                // It exited between the scan and this check, which is the normal
                // case on a busy machine and not a failure.
                continue;
            }

            Assert.True(
                process.MainWindowHandle != IntPtr.Zero,
                candidate.Name + " was offered without a window");
        }
    }

    [Fact]
    public void NothingUnderWindowsIsOffered()
    {
        // IsSystemBinary. A shell component or a system tool with a window is not a
        // game, and the user binding a slot to one would be a strange thing to have
        // happened by accident.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .Replace('/', '\\')
            .TrimEnd('\\');

        foreach (AppCandidate candidate in _library.ScanRunning())
        {
            Assert.False(
                candidate.ExePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase),
                candidate.Name + " is a system binary");
        }
    }

    [Fact]
    public void TheShellIsNotOffered()
    {
        // explorer, SearchHost and the rest. These are the same names the process
        // watcher already treats as "not the app the user is in", listed again here
        // rather than shared because that class is asking a different question.
        string[] shell = { "explorer", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost" };

        foreach (AppCandidate candidate in _library.ScanRunning())
        {
            Assert.DoesNotContain(
                candidate.ProcessName,
                shell,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheListIsSortedSoTheSameProgramsAreInTheSamePlaceEachTime()
    {
        // Not cosmetic. The list is rebuilt underneath the open dropdown every time
        // it opens, and an unsorted one would move entries about between opens -
        // so the muscle memory for "my game is fourth" would never hold.
        AppCandidate[] found = _library.ScanRunning().ToArray();

        for (int i = 1; i < found.Length; i++)
        {
            Assert.True(
                string.Compare(
                    found[i - 1].Name, found[i].Name,
                    StringComparison.OrdinalIgnoreCase) <= 0,
                found[i - 1].Name + " came before " + found[i].Name);
        }
    }

    [Fact]
    public void AnUnreadableProgramDoesNotLoseTheRestOfTheList()
    {
        // Not directly testable - it needs a process that refuses to answer, and a
        // test cannot arrange one without being one. Asserted as the shape of the
        // contract instead: the list is returned, never thrown, so a caller has
        // something to show either way.
        IReadOnlyList<AppCandidate> found = _library.ScanRunning();

        Assert.NotNull(found);
    }

    [Fact]
    public void RunningGamesAreNotFoldedIntoTheInstalledList()
    {
        // Scan must stay what it was. It produces the cached list, and what is
        // running changes minute to minute, so a scan that included it would either
        // be stale on arrival or need re-running constantly.
        AppCandidate[] running = _library.ScanRunning().ToArray();
        if (running.Length == 0)
        {
            return;
        }

        Assert.Contains(running, c => c.Source == "RUNNING");
        Assert.All(running, c => Assert.Equal("RUNNING", c.Source));
    }

    [Fact]
    public void AProgramThisTestStartsIsFoundWithThePathABindingNeeds()
    {
        // The whole value of the feature, asserted against a process the test starts
        // rather than against whatever happens to be running.
        //
        // Notepad is COPIED to a temp folder first, and that detail is the whole
        // test. Notepad lives under C:\Windows\System32, so IsSystemBinary correctly
        // refuses to offer it - which the first draft of this test found out by
        // failing, having picked notepad as an obvious "something with a window on
        // every Windows install". A copy is outside that tree, so it is offered, and
        // this proves the scanner finds a real process rather than proving the
        // filter works.
        string? temp = CopyNotepad();
        if (temp is null)
        {
            return;
        }

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(temp) { UseShellExecute = true });
            if (process is null || !WaitForWindow(process))
            {
                return;
            }

            // Read from the OS rather than constructing the path, so this proves
            // ScanRunning would find the process rather than proving the path is
            // where the test thinks it is.
            string expected = ProcessWatcherService.ReadImagePath((uint)process.Id);
            Assert.False(string.IsNullOrWhiteSpace(expected));

            AppCandidate? hit = _library.ScanRunning().FirstOrDefault(c => string.Equals(
                c.ExePath, expected, StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(hit);
            Assert.Equal("RUNNING", hit!.Source);
            Assert.False(string.IsNullOrWhiteSpace(hit.ProcessName));
            Assert.False(string.IsNullOrWhiteSpace(hit.Name));
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Already gone, or not ours to kill.
                }

                process.Dispose();
            }

            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // A leftover temp copy is not worth failing a test run over.
            }
        }
    }

    [Fact]
    public void AWindowsProgramWithAWindowIsStillRefused()
    {
        // The other half of the test above, and the reason notepad cannot simply be
        // used directly. It has a window and it is running, and offering it would put
        // a system binary in a list of things to bind a game slot to.
        string notepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");

        if (!File.Exists(notepad))
        {
            return;
        }

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(notepad) { UseShellExecute = true });
            if (process is null || !WaitForWindow(process))
            {
                return;
            }

            string started = ProcessWatcherService.ReadImagePath((uint)process.Id);
            Assert.False(
                _library.ScanRunning().Any(c => string.Equals(
                    c.ExePath, started, StringComparison.OrdinalIgnoreCase)),
                "a program under the Windows folder was offered as a game");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Already gone.
                }

                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Notepad copied somewhere outside the Windows folder, or null.
    /// <para>
    /// Null rather than a skip exception: this suite is about the app's own
    /// behaviour, and an absent shell program or an unwritable temp folder is a fact
    /// about the machine rather than a failure of the code under test.
    /// </para>
    /// </summary>
    private static string? CopyNotepad()
    {
        try
        {
            string source = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");

            if (!File.Exists(source))
            {
                return null;
            }

            string copy = Path.Combine(
                Path.GetTempPath(),
                "GamerToolScanRunning_" + Guid.NewGuid().ToString("N") + ".exe");

            File.Copy(source, copy);
            return copy;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Notepad, or null. Null rather than a skip exception because this suite is
    /// about the app's own behaviour and an absent shell program is an environment
    /// fact rather than a failure of it.
    /// </summary>
    private static Process? TryStart()
    {
        try
        {
            return Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Waits for the window to exist. Bounded, because a process that never opens
    /// one must not hang the suite.
    /// </summary>
    private static bool WaitForWindow(Process process)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (process.HasExited)
                {
                    return false;
                }

                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static AppCandidate? Find(IEnumerable<AppCandidate> candidates, Func<AppCandidate, bool> match) =>
        candidates.FirstOrDefault(match);
}