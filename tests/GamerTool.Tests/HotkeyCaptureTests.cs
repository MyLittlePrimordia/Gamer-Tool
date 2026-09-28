using System.Windows.Input;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;


namespace GamerTool.Tests;

/// <summary>
/// The rules the keycap follows while it is listening for a new binding.
/// <para>
/// The reason these are pinned is that a key event cannot be raised from a test,
/// and the bugs here are all invisible until a human holds Alt down. The one
/// that mattered most: holding Alt makes WPF report <see cref="Key.System"/> and
/// put the real key in <c>SystemKey</c>, so reading the first one gave a live,
/// persisted, never firing binding spelled "ALT+SYSTEM". Ctrl worked, Alt did
/// not, and the difference looks like nothing at all in the source.
/// </para>
/// </summary>
public class HotkeyCaptureTests
{
    // ── The Alt trap ───────────────────────────────────────────────────────────

    [Fact]
    public void AltIsReadFromSystemKeyNotKey()
    {
        // What WPF hands over for Alt+1.
        Assert.Equal(Key.D1, HotkeyService.ResolveKey(Key.System, Key.D1));
    }

    [Fact]
    public void AltComboRendersTheRealKeyAndNotTheWordSystem()
    {
        Key key = HotkeyService.ResolveKey(Key.System, Key.D1);
        string text = HotkeyService.FromInput(key, HotkeyModifiers.Alt);

        Assert.Equal("ALT+1", text);
        Assert.DoesNotContain("SYSTEM", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AltComboWithALetterWorksToo()
    {
        Assert.Equal(
            "ALT+K",
            HotkeyService.FromInput(HotkeyService.ResolveKey(Key.System, Key.K), HotkeyModifiers.Alt));
    }

    [Fact]
    public void EveryModifierStillResolvesToItsOwnKey()
    {
        // Only the System pair is special. A normal key must come back untouched.
        Assert.Equal(Key.D1, HotkeyService.ResolveKey(Key.D1, Key.None));
        Assert.Equal(Key.F7, HotkeyService.ResolveKey(Key.F7, Key.None));
    }

    [Fact]
    public void SystemWithNoRealKeyStaysSystem()
    {
        // Alt alone, released before the key landed. Nothing to substitute.
        Assert.Equal(Key.System, HotkeyService.ResolveKey(Key.System, Key.System));
    }

    [Fact]
    public void AnUnresolvedSystemKeyCannotBecomeABinding()
    {
        // Belt and braces: even if a caller forgets to resolve, there is no way to
        // write "ALT+SYSTEM" into a slot.
        Assert.Equal(string.Empty, HotkeyService.FromInput(Key.System, HotkeyModifiers.Alt));
    }

    [Fact]
    public void AModifierOnItsOwnCannotBecomeABinding()
    {
        Assert.Equal(string.Empty, HotkeyService.FromInput(Key.LeftAlt, HotkeyModifiers.Alt));
        Assert.Equal(string.Empty, HotkeyService.FromInput(Key.LWin, HotkeyModifiers.Win));
        Assert.Equal(string.Empty, HotkeyService.FromInput(Key.None, HotkeyModifiers.Control));
    }

    // ── What counts as a modifier ─────────────────────────────────────────────

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightCtrl)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LWin)]
    [InlineData(Key.RWin)]
    [InlineData(Key.System)]
    [InlineData(Key.None)]
    public void ModifiersAreModifiers(Key key)
    {
        Assert.True(HotkeyService.IsModifierKey(key));
        Assert.Equal(HotkeyCapture.NeedsModifier, HotkeyService.ClassifyCapture(key, HotkeyModifiers.None));
    }

    [Theory]
    [InlineData(Key.D1)]
    [InlineData(Key.A)]
    [InlineData(Key.Space)]
    [InlineData(Key.F5)]
    public void OrdinaryKeysAreNotModifiers(Key key)
    {
        Assert.False(HotkeyService.IsModifierKey(key));
    }

    // ── F-keys bind alone, nothing else does ──────────────────────────────────

    [Theory]
    [InlineData(Key.F1)]
    [InlineData(Key.F5)]
    [InlineData(Key.F12)]
    [InlineData(Key.F24)]
    public void FunctionKeysBindWithoutAModifier(Key key)
    {
        Assert.True(HotkeyService.IsFunctionKey(key));
        Assert.Equal(HotkeyCapture.Bind, HotkeyService.ClassifyCapture(key, HotkeyModifiers.None));
    }

    [Theory]
    [InlineData(Key.D0)]
    [InlineData(Key.D9)]
    [InlineData(Key.A)]
    [InlineData(Key.Z)]
    [InlineData(Key.Space)]
    [InlineData(Key.NumPad0)]
    public void BareCharactersAndDigitsAreRefused(Key key)
    {
        // Binding these bare would swallow typing in every other app.
        Assert.False(HotkeyService.IsFunctionKey(key));
        Assert.Equal(HotkeyCapture.NeedsModifier, HotkeyService.ClassifyCapture(key, HotkeyModifiers.None));
    }

    [Fact]
    public void TheKeyAfterF24IsNotAFunctionKey()
    {
        // Guards the range from spilling. WPF's Key stops at F24, and the enum
        // continues into the numpad and OEM keys straight after it.
        Assert.Equal(Key.F24, (Key)((int)Key.F24));
        Assert.True(HotkeyService.IsFunctionKey(Key.F24));
        Assert.False(HotkeyService.IsFunctionKey(Key.NumLock));
        Assert.Equal(HotkeyCapture.NeedsModifier, HotkeyService.ClassifyCapture(Key.NumLock, HotkeyModifiers.None));
    }

    // ── Cancel and clear ──────────────────────────────────────────────────────

    [Fact]
    public void EscapeCancelsEvenWhileAModifierIsHeld()
    {
        Assert.Equal(
            HotkeyCapture.Cancel,
            HotkeyService.ClassifyCapture(Key.Escape, HotkeyModifiers.Control | HotkeyModifiers.Shift));
    }

    [Theory]
    [InlineData(Key.Back)]
    [InlineData(Key.Delete)]
    public void BackspaceAndDeleteClearTheSlot(Key key)
    {
        Assert.Equal(HotkeyCapture.Clear, HotkeyService.ClassifyCapture(key, HotkeyModifiers.None));
        Assert.Equal(HotkeyCapture.Clear, HotkeyService.ClassifyCapture(key, HotkeyModifiers.Control));
    }

    [Fact]
    public void BackspaceIsNotRefusedAsABareKey()
    {
        // It has to reach Clear before the "needs a modifier" rule, or Backspace
        // with nothing held would just say MOD + KEY forever.
        Assert.Equal(
            HotkeyCapture.Clear,
            HotkeyService.ClassifyCapture(Key.Back, HotkeyModifiers.None));
    }

    // ── The shape of a rendered binding ──────────────────────────────────────

    [Theory]
    [InlineData(Key.D1, HotkeyModifiers.Control, "CTRL+1")]
    [InlineData(Key.D4, HotkeyModifiers.Alt, "ALT+4")]
    [InlineData(Key.F2, HotkeyModifiers.None, "F2")]
    [InlineData(Key.F7, HotkeyModifiers.Control | HotkeyModifiers.Shift, "CTRL+SHIFT+F7")]
    [InlineData(Key.S, HotkeyModifiers.Control | HotkeyModifiers.Alt, "CTRL+ALT+S")]
    [InlineData(Key.Q, HotkeyModifiers.Win, "WIN+Q")]
    public void BindingsRenderInAStableOrder(Key key, HotkeyModifiers mods, string expected)
    {
        Assert.Equal(expected, HotkeyService.FromInput(key, mods));
    }

    [Fact]
    public void RenderedBindingsAreStableAcrossNormalise()
    {
        // The value that is stored and the value that is compared for a clash have
        // to agree, or two slots can hold the same chord and only one of them
        // fires.
        foreach (HotkeyModifiers mods in new[]
                 {
                     HotkeyModifiers.None,
                     HotkeyModifiers.Control,
                     HotkeyModifiers.Alt,
                     HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win
                 })
        {
            foreach (Key key in new[] { Key.D1, Key.A, Key.F3, Key.Space })
            {
                string text = HotkeyService.FromInput(key, mods);
                if (text.Length == 0)
                {
                    continue;
                }

                Assert.True(HotkeyService.TryParse(text, out HotkeyModifiers back, out Key backKey));
                Assert.Equal(mods, back);
                Assert.Equal(key, backKey);
            }
        }
    }

    [Fact]
    public void AltCombinationSurvivesAFullRoundTrip()
    {
        // The regression that shipped: an Alt combo that displayed and saved, and
        // then could not be parsed back, so the slot was dead on arrival.
        string text = HotkeyService.FromInput(HotkeyService.ResolveKey(Key.System, Key.F4), HotkeyModifiers.Alt);

        Assert.Equal("ALT+F4", text);
        Assert.True(HotkeyService.TryParse(text, out HotkeyModifiers mods, out Key key));
        Assert.Equal(HotkeyModifiers.Alt, mods);
        Assert.Equal(Key.F4, key);
    }

    // ── What counts as a stored binding ──────────────────────────────────────

    [Theory]
    [InlineData("ALT+SYSTEM")]
    [InlineData("CTRL+SYSTEM")]
    [InlineData("SYSTEM")]
    [InlineData("ALT+LEFTALT")]
    [InlineData("CTRL+LWIN")]
    [InlineData("ALT+NOTAKEY")]
    [InlineData("NOPE+1")]
    public void UnpressableStoredBindingsAreRejected(string text)
    {
        // Key.System and the modifier names are all genuine members of the Key
        // enum, so a plain "is this a known key" check waves them through. That is
        // exactly how ALT+SYSTEM became a saved binding that displayed like a
        // chord and never fired.
        Assert.False(HotkeyService.TryParse(text, out _, out _));
        Assert.False(HotkeyService.IsBindable(text));
    }

    [Theory]
    [InlineData("ALT+1")]
    [InlineData("CTRL+SHIFT+F7")]
    [InlineData("SHIFT+3")]
    [InlineData("F2")]
    [InlineData("WIN+Q")]
    [InlineData("NUM5")]
    [InlineData("CTRL+ALT+S")]
    [InlineData("SPACE")]
    public void RealStoredBindingsSurvive(string text)
    {
        Assert.True(HotkeyService.IsBindable(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NoBindingIsNotABinding(string? text)
    {
        Assert.False(HotkeyService.IsBindable(text));
    }

    // ── The panic key ─────────────────────────────────────────────────────────

    [Fact]
    public void ThePanicKeyIsOnByDefault()
    {
        // A safety net that has to be configured before it works is not one.
        Assert.Equal("CTRL+ALT+F12", new AppSettings().EmergencyHotkey);
    }

    [Fact]
    public void TheDefaultPanicKeyIsARealBindableChord()
    {
        Assert.True(HotkeyService.IsBindable(new AppSettings().EmergencyHotkey));
    }

    [Fact]
    public void APanicKeyThatCannotBePressedIsDroppedOnLoad()
    {
        // Same rule as a slot, and for the same reason: a panic key the user
        // believes they have is worse than no panic key at all.
        AppSettings settings = new() { EmergencyHotkey = "ALT+SYSTEM" };

        ProfileManager.Normalize(settings);

        Assert.Equal(string.Empty, settings.EmergencyHotkey);
    }

    [Theory]
    [InlineData("CTRL+ALT+F12")]
    [InlineData("F12")]
    [InlineData("WIN+SHIFT+F4")]
    public void ARealPanicKeySurvivesNormalisation(string text)
    {
        AppSettings settings = new() { EmergencyHotkey = text };

        ProfileManager.Normalize(settings);

        Assert.Equal(text, settings.EmergencyHotkey);
    }

    [Fact]
    public void EscapeIsTheCancelKeyAndSoCannotAlsoBeABinding()
    {
        // It cancels capture, so there is no gesture that could ever bind it. The
        // token form is "Escape", not "ESC", which is worth pinning because the
        // two look the same and only one of them parses.
        Assert.Equal(HotkeyCapture.Cancel, HotkeyService.ClassifyCapture(Key.Escape, HotkeyModifiers.None));
        Assert.Equal(HotkeyCapture.Cancel, HotkeyService.ClassifyCapture(Key.Escape, HotkeyModifiers.Control));
        Assert.False(HotkeyService.IsBindable("CTRL+ESC"));
        Assert.False(HotkeyService.IsBindable("ESC"));
    }

    [Fact]
    public void APanicKeyCanBeUnsetDeliberately()
    {
        // Clearing it has to stick, or there would be no way to turn it off.
        AppSettings settings = new() { EmergencyHotkey = string.Empty };

        ProfileManager.Normalize(settings);

        Assert.Equal(string.Empty, settings.EmergencyHotkey);
    }
}


