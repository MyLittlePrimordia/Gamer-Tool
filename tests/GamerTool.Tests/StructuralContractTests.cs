using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The structural contract the window is held to.
/// <para>
/// This is deliberately about <em>shape</em> rather than behaviour. Around thirty
/// of the test files in this project assert on the source text of the partials -
/// they look up a method by its declaration and read its body - so a refactor that
/// moves or renames a member breaks the suite without changing a single thing the
/// user can see. Those tests say so where they are written one at a time; this one
/// says it once, in one place, so the constraint is visible before a change is made
/// rather than discovered after it.
/// </para>
/// <para>
/// It is not a substitute for the behavioural tests. It is the tripwire for the one
/// class of change - moving a member to another file - that the behavioural tests
/// cannot see and the source-text tests can only report one at a time.
/// </para>
/// </summary>
public class StructuralContractTests
{
    /// <summary>
    /// Every file the window is made of. The partial split is by feature, and the
    /// split itself is load-bearing: the source-text tests name these files by
    /// path, so a partial moved into a subfolder, renamed, or folded into a
    /// neighbour breaks the suite even when the code is identical.
    /// </summary>
    private static readonly string[] WindowPartials =
    {
        "MainWindow.xaml.cs",
        "MainWindow.Slots.cs",
        "MainWindow.Audio.cs",
        "MainWindow.Display.cs",
        "MainWindow.Night.cs",
        "MainWindow.Backlight.cs",
        "MainWindow.Setup.cs",
        "MainWindow.Modal.cs",
        "MainWindow.Backup.cs",
    };

    [Fact]
    public void EveryWindowPartialIsStillWhereTheTestsLookForIt()
    {
        string app = AppRoot();

        foreach (string partial in WindowPartials)
        {
            Assert.True(
                File.Exists(Path.Combine(app, partial)),
                partial + " has moved. Around thirty test files read these partials "
                + "by path, so moving one breaks the suite without changing behaviour.");
        }

        // And nothing new has appeared beside them. A tenth partial is not wrong in
        // itself, but it is the shape of change this contract exists to make
        // somebody stop and think about, because every source-text test that scans
        // a partial is now scanning an incomplete picture of the window.
        //
        // Exact equality rather than a subset check, so the assertion fails in both
        // directions: a partial that vanished and a partial that appeared are both
        // worth a second look.
        string[] actual = Directory.GetFiles(app, "MainWindow*.cs")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            WindowPartials.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            actual);
    }

    [Fact]
    public void TheWindowIsStillOnePartialClass()
    {
        // Every file declares the same class. A partial that was changed into a
        // nested class, a separate window or a user control would stop being part
        // of the window silently, and the x:Name lookups in the XAML would fail at
        // runtime rather than at build time.
        //
        // Matched on the declaration without the base list, because only one of the
        // nine carries ": Window" - the rest are bare partials, and there is no
        // reason for the one that states the base type to be a particular file.
        foreach (string partial in WindowPartials)
        {
            string source = File.ReadAllText(Path.Combine(AppRoot(), partial));

            Assert.Contains(
                "partial class MainWindow",
                source,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The members that are addressed by name in the source text of another file.
    /// </summary>
    /// <remarks>
    /// A rename here is a compile error if the caller is C# and a silent pass-then-
    /// fail if the caller is a test reading the file. Listed once so the cost of a
    /// rename is visible in one place. This does not replace the tests that use
    /// these names - it makes sure a name that is depended on cannot disappear
    /// without someone seeing this list first.
    /// </remarks>
    [Theory]
    [InlineData("MainWindow.xaml.cs", "private void Commit()")]
    [InlineData("MainWindow.xaml.cs", "private void RefreshPresetBoxes()")]
    [InlineData("MainWindow.xaml.cs", "private void UpdatePresetChrome()")]
    [InlineData("MainWindow.xaml.cs", "private void UpdateLiveLabels()")]
    [InlineData("MainWindow.xaml.cs", "private void RefreshTraySlots()")]
    [InlineData("MainWindow.xaml.cs", "internal static TimeSpan FxPollInterval(bool onScreen)")]
    [InlineData("MainWindow.Slots.cs", "private void PlaySlot(")]
    [InlineData("MainWindow.Slots.cs", "private void BuildSlots()")]
    [InlineData("MainWindow.Slots.cs", "private void RegisterHotkeys()")]
    [InlineData("MainWindow.Slots.cs", "private async void OnHotkeyPressed(HotkeyBinding binding)")]
    [InlineData("MainWindow.Slots.cs", "private void WireKeycap(TextBox box, KeycapCapture which)")]
    [InlineData("Services/SlotService.cs", "public static string NameForCurrentGame(WatchedWindow window)")]
    [InlineData("MainWindow.Slots.cs", "internal static bool ShouldPauseForFocusLoss(")]
    [InlineData("MainWindow.Slots.cs", "private static void ResolveAppIcons(List<AppCandidate> candidates)")]
    [InlineData("MainWindow.Slots.cs", "private void WarmAppIcons()")]
    [InlineData("MainWindow.Audio.cs", "private void QueueLiveTune()")]
    [InlineData("MainWindow.Audio.cs", "private void PushLiveTune()")]
    [InlineData("MainWindow.Audio.cs", "private void ApplyEngineState(FxSoundState state)")]
    [InlineData("MainWindow.Audio.cs", "private void UpdateFxBanner(FxSoundState? snapshot)")]
    [InlineData("MainWindow.Display.cs", "public void GoScreenNeutral(bool emergency = false)")]
    [InlineData("MainWindow.Display.cs", "public void GoScreenStandDown()")]
    [InlineData("MainWindow.Display.cs", "private void StandDownScreen(bool restorePanel, bool emergency = false)")]
    [InlineData("MainWindow.Setup.cs", "private void ApplyWatchState()")]
    [InlineData("MainWindow.Night.cs", "internal static bool ShouldReassertNightAfterStandDown(")]
    public void AMemberAddressedByNameFromElsewhereStillExists(string file, string declaration)
    {
        string source = File.ReadAllText(Path.Combine(AppRoot(), file));

        Assert.Contains(declaration, source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUndoFlagIsStillDeclaredInTheSlotPartial()
    {
        // Pinned on its own because it is the one private *field* a test reads by
        // name, and because it is the field a refactor of the undo state would most
        // naturally move. Whatever ends up owning the value, the declaration has to
        // stay here: UndoLoadTests reads this file and asserts on this exact text.
        string slots = File.ReadAllText(Path.Combine(AppRoot(), "MainWindow.Slots.cs"));

        Assert.Contains("private bool _undoAvailable;", slots, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two paths where a preset actually becomes live go through the fan-out.
    /// </summary>
    /// <remarks>
    /// This is the defect the fan-out exists for, stated as a rule rather than left
    /// to the next reader. Both of these used to refresh some of the surfaces that
    /// render which preset is loaded and not others - neither touched the dropdown -
    /// so a preset loaded by a slot key could leave the box naming the previous one
    /// until something else happened to rebuild it. Nothing caught it: the tests
    /// that cover these methods assert on the apply, not on what the page shows
    /// afterwards, and the refresh methods themselves cannot be reached from a test
    /// without a window.
    /// <para>
    /// Caller-side, because there is nowhere else to put it. What it guarantees is
    /// narrow and worth being honest about: that these two do not go back to naming
    /// surfaces one at a time.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("MainWindow.Display.cs", "private bool ApplyDisplay(DisplayPreset preset, string monitorDevice, bool announce)")]
    [InlineData("MainWindow.Audio.cs", "private void ApplyAudioCore(AudioPreset preset, bool announce, HotkeySlot? routeFrom)")]
    public void APresetBecomingLiveGoesThroughTheFanOut(string file, string signature)
    {
        string source = File.ReadAllText(Path.Combine(AppRoot(), file));

        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);

        // The body runs to the next member declaration, which is the same way the
        // other source-reading tests in this project bound a method.
        int end = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        string body = end < 0 ? source[at..] : source[at..end];

        Assert.Contains("RefreshActivePreset();", body, StringComparison.Ordinal);

        Assert.DoesNotContain("UpdateLiveLabels();", body, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdatePresetChrome();", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The repository root, found the same way the other source-reading tests find
    /// it: by walking up until the app project file turns up.
    /// </summary>
    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "app");
    }
}
