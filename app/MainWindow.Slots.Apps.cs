using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GamerTool.Models;
using GamerTool.Services;
using GamerTool.UI;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;

namespace GamerTool;

// Which programs a slot can point at: installed-app scanning, icons and the app picker.
//
// One of the MainWindow.Slots partials. They are all the same class; the split is
// navigational, and the seams are the ones the features already had. Nothing here
// is a separate module and nothing here can be used without the rest.

public partial class MainWindow : Window
{
    /// <summary>
    /// Fills in the icon on any row that does not have one yet.
    /// <para>
    /// Cache first, then read from disk if it is not there. That order is the fix,
    /// and the second half is not optional.
    /// </para>
    /// <para>
    /// A version of this only looked in the cache, on the theory that a background
    /// pass had already filled it. Measured against a cold cache: no row got an icon,
    /// and every one of them had one available. Anything that opened a picker
    /// before that background pass finished saw an empty list, and there was no
    /// recovery, because the picker had no way of getting an icon on its own. The
    /// running-games entries were the worst of it - they are added as the dropdown
    /// opens and were never in the pass at all, so the games actually being played
    /// were permanently blank.
    /// </para>
    /// <para>
    /// Reading here is what the original code did and it worked, so it stays as the
    /// floor: the picker can always get its own icons. The background pass is kept
    /// because it is what stops that floor from costing anything - when it has
    /// finished, which is the usual case by the time a user opens a picker, every
    /// row below is a dictionary hit.
    /// </para>
    /// </summary>
    private static void ResolveAppIcons(List<AppCandidate> candidates)
    {
        foreach (AppCandidate candidate in candidates)
        {
            if (candidate.Icon is not null || candidate.ExePath.Length == 0)
            {
                continue;
            }

            // The three modes have no file behind them, so there is nothing to
            // pull an icon out of. Without this each would try to read its own
            // marker string as a path.
            if (candidate.ExePath == SelfMarker
                || candidate.ExePath == BrowseMarker
                || candidate.ExePath == AnyGameMarker)
            {
                continue;
            }

            if (IconFactory.TryGetCachedAppIcon(candidate.ExePath, IconFactory.WarmSize, out var icon))
            {
                candidate.Icon = icon;
                continue;
            }

            // Not warmed yet. Read it now rather than leaving the row blank: this is
            // the original behaviour, it is slower, and it is the reason the picker
            // shows icons at all rather than depending on a race it does not control.
            candidate.Icon = IconFactory.ExtractAppIcon(candidate.ExePath, IconFactory.WarmSize);
        }
    }


    /// <summary>
    /// Fills in icons on a background thread and reports back to the dispatcher.
    /// <para>
    /// For rows that arrive after the scan, which the warm pass cannot have seen.
    /// There are only ever a handful - the programs running right now - so unlike
    /// the library-wide pass this is not competing with anything, and it is worth
    /// moving off the dispatcher anyway so a slow icon cannot hold the list the user
    /// is looking at.
    /// </para>
    /// </summary>
    private static async System.Threading.Tasks.Task ResolveIconsOffThreadAsync(IReadOnlyList<AppCandidate> candidates)
    {
        foreach (AppCandidate candidate in candidates)
        {
            if (candidate.Icon is not null || candidate.ExePath.Length == 0)
            {
                continue;
            }

            string path = candidate.ExePath;
            System.Windows.Media.Imaging.BitmapSource? icon =
                await System.Threading.Tasks.Task.Run(
                    () => IconFactory.ExtractAppIcon(path, IconFactory.WarmSize));

            if (icon is null)
            {
                continue;
            }

            // Back on the dispatcher, which is where the setter's change
            // notification has to be raised from.
            candidate.Icon = icon;
            await System.Threading.Tasks.Task.Yield();
        }
    }


    /// <summary>
    /// Reads every icon in the library into the cache, on a background thread.
    /// <para>
    /// Fired once a scan has settled and never awaited. Every slot's dropdown is
    /// built from the same <see cref="AppCandidate"/> objects this list holds rather
    /// than copies of them, so one pass here is enough for every picker in the app -
    /// and because it only fills a dictionary, it does not matter that the lists are
    /// already bound and that nothing is told about it.
    /// </para>
    /// <para>
    /// About a second of disk reads for a large library, on a thread that is not the
    /// one drawing anything.
    /// </para>
    /// </summary>
    private void WarmAppIcons()
    {
        List<AppCandidate> warming = _appList;

        _ = System.Threading.Tasks.Task.Run(() =>
            IconFactory.WarmAppIcons(
                warming
                    .Where(c => c.ExePath.Length > 0
                        && c.ExePath != SelfMarker
                        && c.ExePath != BrowseMarker
                        && c.ExePath != AnyGameMarker)
                    .Select(c => c.ExePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)));
    }


    /// <summary>
    /// Puts the currently running programs into an open dropdown, above the
    /// installed ones.
    /// <para>
    /// Binding a slot to a game is only as good as the path it was bound to, and
    /// scanning the registry and the Steam library is a guess at it. This asks the
    /// machine instead, which is how a game installed somewhere unusual, or behind
    /// a launcher, is found at all.
    /// </para>
    /// <para>
    /// Above the installed list because a game the user can see running is the one
    /// they are trying to bind, and it is the entry they would otherwise have to
    /// recognise in a list of several hundred names. Before the "Browse" line and
    /// after the two modes, so the row order the selection code depends on - modes
    /// first, games from index 3 - is unchanged.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task AddRunningAppsAsync(
        ComboBox box, List<AppCandidate> choices, HotkeySlot slot)
    {
        // Guarded rather than done once. DropDownOpened fires every time the list is
        // opened, and without this the list would grow by one copy of every running
        // program each time.
        choices.RemoveAll(c => c.Source == "RUNNING");

        if (_quitting)
        {
            return;
        }

        IReadOnlyList<AppCandidate> running;
        try
        {
            // Off the dispatcher. This walks every process on the machine and reads
            // an image path for each one with a window, which is enough to be felt
            // as a hitch if it ran on the thread the dropdown is drawing on.
            running = await System.Threading.Tasks.Task.Run(() => _library.ScanRunning());
        }
        catch (Exception ex)
        {
            TraceLog.Write("RUNNING", ex);
            return;
        }

        if (_quitting || running.Count == 0)
        {
            return;
        }

        // Whatever the dropdown is currently showing is preserved, because it is
        // rebuilt underneath the open list. Without this, opening the dropdown
        // cleared a selection the user had already made.
        object? wasSelected = box.SelectedItem;
        int wasIndex = box.SelectedIndex;

        int insertAt = choices.Count(c => c.Source is "NONE" or "SELF" or "ANY");
        choices.InsertRange(insertAt, running);

        // Icons for these, off the dispatcher, before they go on screen.
        //
        // They are the rows a user is most likely to want - the game they have
        // open right now - and they are the one set the scan-time warm pass never
        // sees, because they do not exist until this moment. They were left to the
        // dropdown's own resolve, which for a while could only read a cache it had
        // no reason to be warm for, so the games actually being played came up
        // blank while the installed ones did not.
        _ = ResolveIconsOffThreadAsync(running);

        box.Items.Refresh();

        // Re-found by path rather than by index, because inserting the running
        // programs shifts every index below the insertion point - and the code that
        // reads the selection by index would then pick the wrong game.
        if (wasSelected is AppCandidate previous)
        {
            foreach (object item in box.Items)
            {
                if (item is AppCandidate candidate
                    && candidate.Source == previous.Source
                    && string.Equals(candidate.ExePath, previous.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = candidate;
                    break;
                }
            }
        }
        else if (wasIndex >= 0)
        {
            box.SelectedIndex = wasIndex < insertAt ? wasIndex : wasIndex + running.Count;
        }
    }


    private ComboBox AppCombo(HotkeySlot slot)
    {
        List<AppCandidate> choices = new()
        {
            new AppCandidate { Name = "No game", ExePath = string.Empty, ProcessName = string.Empty, Source = "NONE" },
            new AppCandidate { Name = "Gamer Tool (on start)", ExePath = SelfMarker, ProcessName = string.Empty, Source = "SELF", Icon = IconFactory.LoadWindowIcon() },

            // The wildcard, beside the two other modes rather than at the bottom of
            // the list with several hundred installed games. It is a mode, not a
            // target, and buried under a Steam library it would never be found.
            new AppCandidate { Name = "Any game (fullscreen)", ExePath = AnyGameMarker, ProcessName = string.Empty, Source = "ANY" }
        };
        choices.AddRange(_appList.Where(c => !string.IsNullOrWhiteSpace(c.ExePath)));
        choices.Add(new AppCandidate { Name = "Browse for a file...", ExePath = BrowseMarker, ProcessName = string.Empty, Source = "BROWSE" });

        ComboBox box = new()
        {
            Style = (Style)FindResource("ModernCombo"),
            ItemContainerStyle = (Style)FindResource("ModernComboItem"),
            ItemTemplate = (DataTemplate)FindResource("AppIconItem"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ItemsSource = choices,
            Tag = slot.Id + "|app",

            // The auto-apply explanation lived on a switch that is gone. It is
            // the game dropdown that decides whether a slot loads itself, so it
            // is also the only place the answer can be read from.
            ToolTip = AutoToolTip(slot)
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Game for " + slot.Name);

        // Installed programs have long names and this column is narrow, so the
        // closed box scrolls rather than clipping.
        MarqueeBox.SetAllowMarquee(box, true);

        // Icons are pulled the first time the list is actually opened, not on startup,
        // and off the dispatcher so the drop is not held up by it. By this point
        // the scan has usually already warmed them, so there is nothing to do and
        // the list opens complete.
        box.DropDownOpened += (_, _) => ResolveAppIcons(choices);

        // The running games are put in as the dropdown opens, not baked into the
        // list when the slot row was built. What is running changes minute to
        // minute, and a list that showed the programs open at the time the Hotkeys
        // tab was last built would be the wrong answer by the time the user looked
        // for their game.
        box.DropDownOpened += (_, _) => _ = AddRunningAppsAsync(box, choices, slot);

        // Index 2 is the wildcard, so the installed games start at 3. Hard-coded
        // rather than counted, because a count would silently shift every index
        // below it whenever a mode were added - and the symptom would be a game
        // row that selects the wrong preset.
        int index = slot.IsSelfTarget ? 1 : slot.IsAnyGameTarget ? 2 : 0;
        if (!slot.IsSelfTarget && !slot.IsAnyGameTarget && !string.IsNullOrWhiteSpace(slot.AppExePath))
        {
            index = -1;
            for (int i = 3; i < choices.Count; i++)
            {
                if (string.Equals(choices[i].ExePath, slot.AppExePath, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                AppCandidate manual = new()
                {
                    Name = string.IsNullOrWhiteSpace(slot.AppName) ? "Picked" : slot.AppName,
                    ExePath = slot.AppExePath,
                    ProcessName = AppProfileTools.ProcessNameOf(slot.AppExePath),
                    Source = "MANUAL"
                };
                manual.Icon = IconFactory.ExtractAppIcon(manual.ExePath);
                choices.Insert(3, manual);
                box.ItemsSource = choices;
                index = 3;
            }
        }

        box.SelectedIndex = index;
        box.SelectionChanged += OnSlotAppChanged;
        return box;
    }


    private const string BrowseMarker = "\\browse";


    private const string SelfMarker = "\\self";


    /// <summary>
    /// The wildcard entry in a slot's game dropdown: not bound to any program.
    /// <para>
    /// A marker rather than a path, so it cannot collide with a real file - the
    /// same reason <see cref="SelfMarker"/> exists. Selected here rather than typed,
    /// because it is a mode rather than a target and reading it off the dropdown
    /// says which.
    /// </para>
    /// </summary>
    private const string AnyGameMarker = "\\any";


    /// <summary>
    /// Runs one app-library scan at a time, and runs another afterwards for a
    /// request that arrived while the first was in flight.
    /// </summary>
    /// <remarks>
    /// The sequencing lives in <see cref="ScanCoordinator"/> rather than here, and
    /// that move is the point. The rule this replaces was hand-written in this file
    /// and read plausibly, and reading it plausibly was the only thing checking it:
    /// a scan of the real Steam libraries and the real uninstall registry has no
    /// seam a test can drive, so the coalescing could not be exercised at all. It
    /// had a lost-request window as well - see the remarks on the coordinator for
    /// what it was. Out there the rule is asserted directly, with a fake scan and a
    /// gate the test holds open, and this method is left doing only what belongs to
    /// a window: building the list and warming its icons.
    /// <para>
    /// Built lazily because the run delegate is an instance member: the coordinator
    /// has to be able to call back into <see cref="ScanAppLibraryAsync"/> and
    /// <see cref="WarmAppIcons"/>, neither of which exists until the window does.
    /// </para>
    /// </remarks>
    private ScanCoordinator? _scans;

    private ScanCoordinator Scans => _scans ??= new ScanCoordinator(ScanAppLibraryAsync);

    /// <summary>
    /// One pass: build the list from the machine and sort it.
    /// </summary>
    private async System.Threading.Tasks.Task ScanAppLibraryAsync()
    {
        _appList = (await System.Threading.Tasks.Task.Run(() => _library.Scan()))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // In the background, and only once the scan has settled. This is the pass
        // that makes the dropdowns open with their icons already on them.
        WarmAppIcons();
    }

    /// <param name="force">
    /// Rescan even when the list already has something in it.
    /// <para>
    /// The scan button was calling the non-forcing version, so after the Hotkeys
    /// tab had warmed the list once - which it does every time it is opened - the
    /// button did nothing at all. It still said "SCANNING FOR GAMES" and still
    /// reported a count, so it looked like a scan that had found nothing new,
    /// which is exactly what someone who has just installed a game concludes.
    /// </para>
    /// </param>
    /// <summary>
    /// Builds the app list, unless one is already being built.
    /// <para>
    /// Reached from four places: the scan button, the first time a slot's game
    /// dropdown opens, a restore, and a window state change. The scan walks every
    /// Steam library and every uninstall subkey on the machine, so without the
    /// guard a second caller - which force:true permits, since it deliberately
    /// bypasses the cache - ran it twice at once, and whichever finished last
    /// overwrote the other's result with a list that may have been built mid-scan.
    /// </para>
    /// <para>
    /// Waits for the pass that serves this request, including when it joined one
    /// already running. The handler for the scan button builds the board from
    /// <c>_appList</c> immediately after this returns, so returning before the pass
    /// that answers it had finished would rebuild the board from a list that was
    /// still being replaced underneath it.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task EnsureAppList(bool force = false)
    {
        if (!force && _appList.Count > 0)
        {
            return;
        }

        await Scans.RequestAsync();
    }


    private async void OnScanAppsClick(object sender, RoutedEventArgs e)
    {
        // Disabled for the duration rather than merely guarded, because this is
        // the slowest thing the app does on demand and the only feedback it gave
        // was one line of caption text. The flag above stops the duplicate work;
        // this stops the button looking available while it is happening.
        ScanGamesButton.IsEnabled = false;
        SetRail("SCANNING FOR GAMES", RailTone.Busy);

        try
        {
            await EnsureAppList(force: true);
            BuildSlots();
            SetRail("READY");
            Flash(_appList.Count.ToString(CultureInfo.InvariantCulture) + " games found");
        }
        catch (Exception ex)
        {
            TraceLog.Write("SCAN GAMES", ex);
            SetRail("SCAN FAILED", RailTone.Failed);
            Flash("Could not scan for games", true);
        }
        finally
        {
            ScanGamesButton.IsEnabled = true;
        }
    }
}
