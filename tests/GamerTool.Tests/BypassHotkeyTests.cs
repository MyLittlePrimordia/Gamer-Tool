using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Two keycaps that are not a slot's, and the rule about which one wins a chord.
/// <para>
/// The panic key has always been here and the bypass key is new. The interesting
/// part is not that a second one exists, it is that a contested chord has to go to
/// the same one every time - and that the answer is only ever asked in one place,
/// so the answer is only ever given in one place.
/// </para>
/// <para>
/// Precedence, in order: the panic key, then the bypass key, then slots. The panic
/// key is the one binding whose entire job is to still work when something else has
/// gone wrong, and a bypass is a convenience, so a convenience gives way.
/// </para>
/// </summary>
public class BypassHotkeyTests
{
    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app", "GamerTool.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts)));
    }

    private static string Slots() => Read("app", "MainWindow.Slots.cs");

    private static string Setup() => Read("app", "MainWindow.Setup.cs");

    private static string Settings() => Read("app", "Models", "AppSettings.cs");

    /// <summary>The body of a method, so two tests cannot both pass on one call.</summary>
    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);

        int end = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.Length;
        }

        return source.Substring(at, end - at);
    }

    [Fact]
    public void TheBypassKeyDefaultsToNothing()
    {
        // Deliberately the opposite of the panic key, which defaults to a chord
        // because a safety net that has to be configured before it works is not one.
        // A convenience that claimed a chord nobody chose would take it from whatever
        // the user actually uses it for.
        Assert.Contains(
            "public string BypassHotkey { get; set; } = string.Empty;",
            Settings(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanicKeyIsRegisteredBeforeTheBypassKey()
    {
        // The ordering IS the precedence rule. In the method body rather than
        // somewhere abstract, because this is the only place that decides which of
        // the three wins.
        string body = Body(Slots(), "private void RegisterHotkeys()");

        int panic = body.IndexOf("EmergencyHotkeyId", StringComparison.Ordinal);
        int bypass = body.IndexOf("BypassHotkeyId", StringComparison.Ordinal);
        int slot = body.IndexOf("\"slot:\" + slot.Id", StringComparison.Ordinal);

        Assert.True(panic >= 0, "the panic key is no longer registered here");
        Assert.True(bypass >= 0, "the bypass key is no longer registered here");
        Assert.True(panic < bypass, "the bypass key is being offered before the panic key");
        Assert.True(bypass < slot, "a slot is being offered before the bypass key");
    }

    [Fact]
    public void TheBypassKeyIsClaimedOnlyWhenWindowsAcceptsIt()
    {
        // Released again on failure, as the panic key's own comment explains it
        // must be. Without the release the key stays in `taken`, the slot holding it
        // is skipped, and the user is told the bypass key took it - which is false,
        // because the bypass key did not get it either. Both bindings dead and the
        // message naming the wrong one.
        string body = Body(Slots(), "private void RegisterHotkeys()");

        int claim = body.IndexOf("if (taken.Add(bypassKey))", StringComparison.Ordinal);
        int release = body.IndexOf("taken.Remove(bypassKey);", StringComparison.Ordinal);

        Assert.True(claim >= 0, "the bypass key no longer claims its chord in `taken`");
        Assert.True(release > claim, "a refused bypass key stays in `taken` and kills the slot holding it");
    }

    [Fact]
    public void ARefusedBypassKeyIsReportedRatherThanSilentlyDead()
    {
        // Same reason the slot failures are reported: a binding that is not
        // registered and shows no sign of not being registered is the state this
        // whole area has been repeatedly fixed for.
        string body = Body(Slots(), "private void RegisterHotkeys()");

        Assert.Contains("refusedKeys.Add(_settings.BypassHotkey)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BindingThePanicKeyTakesTheChordOffTheBypassKey()
    {
        // The panic key wins, so pointing it at a chord the bypass key holds has to
        // clear the bypass key - otherwise two bindings claim one chord and only one
        // of them is ever registered.
        string body = Body(Slots(), "private void BindPanicHotkey");

        Assert.Contains("_settings.BypassHotkey = string.Empty;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BindingTheBypassKeyTakesTheChordOffASlot()
    {
        // The rule the slots already use: the thing being pointed at wins, because
        // that is the one the user is actively looking at.
        string body = Body(Slots(), "private void BindBypassHotkey");

        Assert.Contains("clash.Hotkey = string.Empty;", body, StringComparison.Ordinal);
        Assert.Contains("Flash(\"Bypass key took it from \"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBypassKeyIsRefusedThePanicKeysChordRatherThanStealingIt()
    {
        // The asymmetry is the point. Panic beats bypass by taking the chord;
        // bypass loses to panic by refusing. Two keycaps bound to one chord is the
        // state this exists to prevent, and a bypass that silently stole the panic
        // key's would leave the panic keycap showing a chord that does nothing.
        string body = Body(Slots(), "private void BindBypassHotkey");

        Assert.Contains("EndCapture(restore: true);", body, StringComparison.Ordinal);
        Assert.Contains("That is the panic key already", body, StringComparison.Ordinal);
        Assert.Contains("true);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBypassKeyIsHandedFromTheCaptureHandler()
    {
        // Clear has to reach it too, or Backspace on the bypass keycap does nothing
        // at all - which would look like the keycap not listening.
        string body = Body(Setup(), "private void OnPreviewKeyDown");

        Assert.Contains("if (_capturingKeycap == KeycapCapture.Bypass)", body, StringComparison.Ordinal);
        Assert.Contains("BindBypassHotkey(text);", body, StringComparison.Ordinal);
        Assert.Contains("ClearBypassHotkey();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellingABypassCaptureRestoresItsOwnBinding()
    {
        // The restore has to read the bypass key's setting. Left reading the panic
        // key's, Esc on the bypass keycap would show the panic key's chord on it.
        string body = Body(Setup(), "private void EndCapture");

        Assert.Contains("_settings.BypassHotkey", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BothKeycapsShareOneWireUp()
    {
        // Not one pair of handlers each. Two focus pairs that look alike drift, and
        // the second one to be written would not get the fix applied to the first.
        string source = Slots();

        Assert.Contains("private void WireKeycap(TextBox box, KeycapCapture which)", source, StringComparison.Ordinal);

        // And the two entry points are one line each, which is what makes the shared
        // method the only copy.
        Assert.Contains("WireKeycap(PanicKeyBox, KeycapCapture.Panic);", source, StringComparison.Ordinal);
        Assert.Contains("WireKeycap(BypassKeyBox, KeycapCapture.Bypass);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSharedCaptureStateCanSayWhichOfTheTwoItIs()
    {
        // An enum rather than a second bool, because a bool can only name one of
        // them and a keypress asking "which one is this" would get "yes" twice.
        string source = Read("app", "MainWindow.xaml.cs");

        // The enum and its two cases are read as declarations, with comments stripped,
        // because a doc comment above each case legitimately mentions the other one
        // and the test is about which cases exist rather than what is written near
        // them.
        string declarations = Regex.Replace(source, @"///.*?(\r?\n)", string.Empty);

        Assert.Contains("private enum KeycapCapture", declarations, StringComparison.Ordinal);
        // (?m) so ^ means start of line rather than start of string, which is what
        // Assert.Matches gives by default. Without it the pattern can only ever
        // match at the very top of the file and the assertion is decoration.
        Assert.Matches(@"(?m)^\s*Panic,\s*$", declarations);
        Assert.Matches(@"(?m)^\s*Bypass,\s*$", declarations);
        Assert.Contains("private KeycapCapture _capturingKeycap;", declarations, StringComparison.Ordinal);
        Assert.DoesNotContain("private bool _capturingPanic", declarations, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBypassKeyIsHandledByFlippingTheSwitchRatherThanTheSetting()
    {
        // The switch's own handler is the one place that sets EffectsEnabled,
        // commits, queues the engine push and verifies it landed. Four things that
        // all have to happen together, so setting the field here instead would leave
        // the switch and the engine disagreeing.
        string body = Body(Slots(), "private async void OnHotkeyPressed");

        Assert.Contains("BypassTargetId", body, StringComparison.Ordinal);
        Assert.Contains("BypassBox.IsChecked = BypassBox.IsChecked != true;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_settings.EffectsEnabled =", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBypassKeyRefusesToPretendItWorksWithoutFxSound()
    {
        // Said plainly rather than toggling a switch whose push is going to fail.
        string body = Body(Slots(), "private async void OnHotkeyPressed");

        Assert.Contains("if (!_audio.IsInstalled)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBypassKeycapIsOnTheHotkeysTabNotInSettings()
    {
        // The reason the panic key lives on the Hotkeys tab: it was one more row in
        // the settings list, which is what put a scrollbar on a list that had never
        // needed one. Putting the second key there instead would repeat that.
        string xaml = Read("app", "MainWindow.xaml");

        int hotkeysTab = xaml.IndexOf("x:Name=\"SlotScroll\"", StringComparison.Ordinal);

        int panic = xaml.IndexOf("x:Name=\"PanicKeyBox\"", StringComparison.Ordinal);
        int bypass = xaml.IndexOf("x:Name=\"BypassKeyBox\"", StringComparison.Ordinal);

        Assert.True(panic > 0 && bypass > 0, "a keycap has gone missing from the page");
        Assert.True(panic < hotkeysTab, "the panic key moved below the slot list");
        Assert.True(bypass < hotkeysTab, "the bypass key is not beside the panic key");
    }

    [Fact]
    public void TheBypassKeycapAddsNoSettingsRow()
    {
        // The zero scroll rule. This lives in the Hotkeys tab header, so the settings
        // list is untouched and its 13 rows still fit 674 px.
        Assert.DoesNotContain(
            "BypassKeyBox",
            Read("app", "MainWindow.xaml").Substring(
                Read("app", "MainWindow.xaml").IndexOf("x:Name=\"SettingsScroll\"", StringComparison.Ordinal)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheBypassKeycapFitsBesideThePanicOne()
    {
        // The header is a Grid with * / Auto / 18 / Auto, so the keycaps sit in the
        // Auto column and the star absorbs what is left. Pinned as arithmetic rather
        // than as a measurement because the test suite has no WPF layout pass over
        // this header and the failure mode is a clipped keycap rather than a crash.
        string xaml = Read("app", "MainWindow.xaml");
        int at = xaml.IndexOf("x:Name=\"BypassKeyBox\"", StringComparison.Ordinal);
        int panicAt = xaml.IndexOf("x:Name=\"PanicKeyBox\"", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.True(panicAt > 0);

        // They are inside the same horizontal StackPanel, so nothing between them
        // closes a container. Comments carry markup examples and would show up as
        // false positives, so they go before the check.
        string between = Regex.Replace(
            xaml.Substring(panicAt, at - panicAt), @"<!--.*?-->", string.Empty,
            RegexOptions.Singleline);

        Assert.DoesNotContain("</StackPanel>", between, StringComparison.Ordinal);
        Assert.DoesNotContain("</Grid>", between, StringComparison.Ordinal);
    }

    [Fact]
    public void BothKeycapsAreTheSameWidth()
    {
        // They sit side by side, so a mismatch reads as a mistake rather than a
        // choice.
        string xaml = Read("app", "MainWindow.xaml");

        int panic = xaml.IndexOf("x:Name=\"PanicKeyBox\"", StringComparison.Ordinal);
        int bypass = xaml.IndexOf("x:Name=\"BypassKeyBox\"", StringComparison.Ordinal);

        string panicWidth = Regex.Match(xaml.Substring(panic, 200), @"Width=""(\d+)""").Groups[1].Value;
        string bypassWidth = Regex.Match(xaml.Substring(bypass, 200), @"Width=""(\d+)""").Groups[1].Value;

        Assert.Equal(panicWidth, bypassWidth);
    }
}