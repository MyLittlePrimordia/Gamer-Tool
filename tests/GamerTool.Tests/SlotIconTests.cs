using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The icons in a slot's game picker, which has now been wrong twice.
/// <para>
/// It worked before any of this: opening a dropdown ran a loop on the dispatcher
/// that read every game's icon first, so the list was complete by the time it
/// painted. Slow, and correct.
/// </para>
/// <para>
/// Making it non-blocking made it open instantly and fill in over the next few
/// seconds instead - a picker full of blank squares that acquired icons while it
/// was being read, and only turned up the rest once selecting something forced a
/// redraw. Both versions were wrong in the same way, by doing the slow work at the
/// wrong moment: slow work in the open handler freezes the window, slow work spread
/// across the open handler shows an incomplete list.
/// </para>
/// <para>
/// The fix separates them. The reading happens once at scan time, on a background
/// thread, filling a cache and touching no binding. The dropdown then only ever does
/// dictionary lookups. Complete and instant at the same time, which neither of the
/// two earlier versions was.
/// </para>
/// </summary>
[Collection("wpf")]
public class SlotIconTests
{
    [Fact]
    public void The_dropdown_tries_the_cache_before_it_reads_from_disk()
    {
        // Both halves, in order, and the order is the point. Cache first is the fast
        // path and it is what the background pass exists to make the usual case.
        // Falling through to a read is the floor, and it is what makes the picker
        // able to get its own icons rather than depending on a race it does not
        // control - which is the version that shipped and showed nothing.
        string source = File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.Slots.cs")));

        int method = source.IndexOf("private static void ResolveAppIcons(List<AppCandidate> candidates)", StringComparison.Ordinal);
        Assert.True(method > 0, "the icon resolve is no longer a plain method");

        int end = source.IndexOf("private void WarmAppIcons()", method, StringComparison.Ordinal);
        Assert.True(end > method, "could not find the end of the resolve");

        string body = source.Substring(method, end - method);

        int cached = body.IndexOf("TryGetCachedAppIcon", StringComparison.Ordinal);
        int read = body.IndexOf("IconFactory.ExtractAppIcon", StringComparison.Ordinal);

        Assert.True(cached > 0, "the resolve never looks in the cache");
        Assert.True(read > 0, "the resolve can no longer get an icon on its own");
        Assert.True(
            cached < read,
            "the resolve reads from disk before trying the cache, so the warm pass saves nothing");
    }

    [Fact]
    public void The_dropdown_resolve_is_the_thing_the_open_handler_calls()
    {
        string source = File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.Slots.cs")));

        Assert.Contains("box.DropDownOpened += (_, _) => ResolveAppIcons(choices);", source);
    }

    [Fact]
    public void The_slow_read_happens_once_at_scan_time_on_a_background_thread()
    {
        string source = File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.Slots.cs")));

        int scan = source.IndexOf("_appList = (await System.Threading.Tasks.Task.Run(() => _library.Scan()))", StringComparison.Ordinal);
        int warm = source.IndexOf("WarmAppIcons();", StringComparison.Ordinal);

        Assert.True(scan > 0, "the scan no longer builds the app list");
        Assert.True(warm > scan, "the icons are warmed before there is a list to warm");

        // Off the dispatcher, and in one pass rather than per row.
        Assert.Contains("IconFactory.WarmAppIcons(", source);
        Assert.Contains("_ = System.Threading.Tasks.Task.Run(() =>", source);
    }

    [Fact]
    public void WarmingTheCacheTouchesNoBinding()
    {
        // Every slot list is already bound at scan time. A PropertyChanged raised
        // from the warm pass would arrive on a thread WPF is not drawing on, so the
        // pass is allowed to fill the cache and nothing else.
        string factory = File.ReadAllText(FindRepoFile(Path.Combine("app", "Services", "IconFactory.cs")));

        int warm = factory.IndexOf("public static void WarmAppIcons(IEnumerable<string> exePaths)", StringComparison.Ordinal);
        Assert.True(warm > 0, "there is no warm pass");

        int end = factory.IndexOf("public const int WarmSize", warm, StringComparison.Ordinal);
        Assert.True(end > warm, "could not find the end of the warm pass");

        string body = factory.Substring(warm, end - warm);

        Assert.DoesNotContain("PropertyChanged", body);
        Assert.DoesNotContain("AppCandidate", body);
        Assert.Contains("ExtractAppIcon(path, WarmSize)", body);
    }

    [Fact]
    public void A_CacheOnly_Lookup_Reports_Miss_RatherThan_Reading()
    {
        // The distinction the whole design rests on. A miss has to stay a miss, or
        // the dropdown quietly starts doing the slow thing again.
        string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        // A size nothing else in this class asks for, so this is a genuine miss
        // whichever order the tests run in. The cache is process wide and shared,
        // and a sibling test warms the same executable at the picker's size - which
        // made the first version of this assert order-dependent and fail for a
        // reason that had nothing to do with what it was checking.
        const int Size = 23;

        Assert.False(
            GamerTool.Services.IconFactory.TryGetCachedAppIcon(exe, Size, out _),
            "an uncached executable already answered from the cache");

        // Now warm it, and the same lookup must hit without touching the disk.
        GamerTool.Services.IconFactory.ExtractAppIcon(exe, Size);

        Assert.True(
            GamerTool.Services.IconFactory.TryGetCachedAppIcon(exe, Size, out var icon),
            "the icon was read but the cache-only lookup cannot find it");
        Assert.NotNull(icon);
    }

    [Fact]
    public void EveryRowGetsAnIcon_EvenOnAColdCache()
    {
        // The failure, reproduced and then fixed, and this is the test that would
        // have caught it the first time.
        //
        // The resolve was changed to read only from a cache that a background pass
        // was supposed to have filled. Called against a cold cache it produced no
        // icons at all - not wrong ones, none - while every executable it was asked
        // about had one available. So anything that opened a picker before that
        // pass finished saw an empty list and could do nothing about it, because the
        // picker had no way of getting an icon by itself.
        //
        // Three attempts failed to spot this because all three read the code and
        // none of them called it. The reading is cheap; the condition is not.
        //
        // Called by reflection against the real private method on purpose: a test
        // that rebuilt the logic would pass while the logic stayed broken.
        MethodInfo? resolve = typeof(GamerTool.MainWindow)
            .GetMethod("ResolveAppIcons", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(resolve);

        string[] exes = Directory.GetFiles(Environment.SystemDirectory, "*.exe").Take(25).ToArray();
        Assert.True(exes.Length > 5, "not enough real executables to be a fair sample");

        var candidates = new List<GamerTool.Models.AppCandidate>();
        foreach (string exe in exes)
        {
            candidates.Add(new GamerTool.Models.AppCandidate
            {
                Name = Path.GetFileNameWithoutExtension(exe),
                ExePath = exe,
                ProcessName = string.Empty,
                Source = "APP"
            });
        }

        resolve!.Invoke(null, new object[] { candidates });

        int shown = candidates.Count(c => c.Icon is not null);
        int available = candidates.Count(
            c => GamerTool.Services.IconFactory.ExtractAppIcon(c.ExePath, 20) is not null);

        Assert.Equal(available, shown);
        Assert.True(
            shown > 0,
            "the picker got no icons at all from a cold cache. It has to be able to "
            + "get its own, not depend on a background pass having finished first.");
    }

    [Fact]
    public void TheRunningGamesGetTheirIconsToo()
    {
        // The rows a user is most likely to want are the ones running right now, and
        // they are the one set the scan-time warm pass never sees - they do not exist
        // until the dropdown opens. Left to the dropdown's own resolve, and while
        // that could only read a warm cache, the games actually being played came up
        // blank while the installed ones did not.
        string source = File.ReadAllText(FindRepoFile(Path.Combine("app", "MainWindow.Slots.cs")));

        int at = source.IndexOf("private async System.Threading.Tasks.Task AddRunningAppsAsync(", StringComparison.Ordinal);
        Assert.True(at > 0, "the running-apps handler is gone");

        int end = source.IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
        string body = source.Substring(at, end - at);

        Assert.Contains("ResolveIconsOffThreadAsync(running)", body);
        Assert.Contains("Task.Run(", source);
    }

    [Fact]
    public void The_warmed_cache_is_Complete_And_The_Lookup_Is_Free()
    {
        // The two numbers that make the architecture worth having: the slow half
        // costs about a second and happens off the UI thread, and the half that runs
        // while a user is looking at an open dropdown costs nothing measurable.
        string[] exes = Directory.GetFiles(Environment.SystemDirectory, "*.exe").Take(400).ToArray();
        Assert.True(exes.Length > 100, "not enough real executables to measure against");

        var warm = Stopwatch.StartNew();
        GamerTool.Services.IconFactory.WarmAppIcons(exes);
        warm.Stop();

        int hits = 0;
        var lookup = Stopwatch.StartNew();

        foreach (string exe in exes)
        {
            if (GamerTool.Services.IconFactory.TryGetCachedAppIcon(exe, GamerTool.Services.IconFactory.WarmSize, out _))
            {
                hits++;
            }
        }

        lookup.Stop();

        Assert.Equal(exes.Length, hits);

        double total = lookup.Elapsed.TotalMilliseconds;

        Assert.True(
            total < 25.0,
            "looking every icon up took " + total.ToString("0.0")
            + " ms on the dispatcher, which is a visible stutter in an open dropdown "
            + "(the warm pass it followed took " + warm.Elapsed.TotalMilliseconds.ToString("0") + " ms off it)");
    }

    [Fact]
    public void One_icon_is_expensive_enough_that_the_list_can_never_be_read_synchronously()
    {
        // The cost that made the original dispatcher loop untenable, kept so the
        // shape of the code stays defensible. Deliberately the wrong way round: if
        // icon reading ever gets cheap, this fails and says the split can go.
        string[] exes = Directory.GetFiles(Environment.SystemDirectory, "*.exe").Take(400).ToArray();

        var stopwatch = Stopwatch.StartNew();
        int read = 0;

        foreach (string exe in exes)
        {
            if (GamerTool.Services.IconFactory.ExtractAppIcon(exe, 20) is not null)
            {
                read++;
            }
        }

        stopwatch.Stop();

        Assert.Equal(exes.Length, read);

        double fourHundredGames = stopwatch.Elapsed.TotalMilliseconds;

        Assert.True(
            fourHundredGames > 250.0,
            "reading a whole library's icons now costs " + fourHundredGames.ToString("0")
            + " ms. The background warm pass and the cache-only dropdown may no longer be "
            + "necessary - revise the shape rather than deleting this line.");
    }

    [Fact]
    public void The_icon_cache_survives_two_threads_reading_it()
    {
        // The warm pass writes it from a background thread while a dropdown may be
        // reading it. A plain Dictionary corrupts itself here rather than failing,
        // which shows up as one blank icon weeks later and never again.
        string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        int failures = 0;
        var threads = new List<System.Threading.Thread>();

        for (int i = 0; i < 8; i++)
        {
            var thread = new System.Threading.Thread(() =>
            {
                for (int j = 0; j < 40; j++)
                {
                    try
                    {
                        GamerTool.Services.IconFactory.ExtractAppIcon(exe, 20);
                        GamerTool.Services.IconFactory.TryGetCachedAppIcon(exe, 20, out _);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            });

            threads.Add(thread);
            thread.Start();
        }

        foreach (System.Threading.Thread thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(20));
        }

        Assert.Equal(0, failures);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}