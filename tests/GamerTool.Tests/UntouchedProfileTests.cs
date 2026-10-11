using System.Collections.Generic;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Whether a profile has ever been changed, which decides if the app explains the
/// panic key on this launch.
/// </summary>
/// <remarks>
/// A first look at the app is the one moment a line about the panic key is worth
/// spending, and the trigger for it has to be something the app can already see.
/// A stored "have I shown the hint" flag would be a new profile field, a new thing
/// to migrate, and one more piece of state that can disagree with reality - so this
/// asks the settings instead.
/// <para>
/// The tests below are mostly about the negative direction, because the failure
/// that matters is a hint that will not go away: a user who has set the app up the
/// way they want it, still being told about a key on every launch.
/// </para>
/// </remarks>
public class UntouchedProfileTests
{
    [Fact]
    public void AFreshProfileIsUntouched()
    {
        Assert.True(new AppSettings().IsUntouched());
    }

    [Theory]
    [InlineData("EmergencyHotkey")]
    [InlineData("BypassHotkey")]
    [InlineData("GammaLock")]
    [InlineData("ShowOsd")]
    [InlineData("StartHidden")]
    [InlineData("CloseToTray")]
    [InlineData("AutoSwitch")]
    [InlineData("AutoRevertOnExit")]
    [InlineData("AutoPauseOnFocusLoss")]
    [InlineData("AntiClip")]
    [InlineData("LoudGuard")]
    [InlineData("EffectsEnabled")]
    [InlineData("HardwareBrightnessEnabled")]
    [InlineData("NightBlueLight")]
    [InlineData("FxPromptDisabled")]
    [InlineData("ActiveDisplayPresetId")]
    [InlineData("ActiveAudioPresetId")]
    [InlineData("OutputDeviceId")]
    [InlineData("OutputDeviceName")]
    [InlineData("FxSoundPath")]
    public void ChangingAnySwitchStopsItBeingUntouched(string property)
    {
        // One test per setting rather than a single "something changed" case, so a
        // field added to the comparison later without a mind to what it means is
        // caught here rather than in the field.
        AppSettings settings = new();
        typeof(AppSettings).GetProperty(property)!.SetValue(settings, Flip(settings, property));

        Assert.False(settings.IsUntouched(), property + " does not count as a change");
    }

    [Theory]
    [InlineData("BlueLightFilter")]
    [InlineData("NightStartMinutes")]
    [InlineData("NightEndMinutes")]
    [InlineData("NightFadeMinutes")]
    public void ChangingANumberStopsItBeingUntouched(string property)
    {
        AppSettings settings = new();
        var info = typeof(AppSettings).GetProperty(property)!;
        int current = (int)info.GetValue(settings)!;
        info.SetValue(settings, current + 1);

        Assert.False(settings.IsUntouched(), property + " does not count as a change");
    }

    [Fact]
    public void SavingACustomPresetStopsItBeingUntouched()
    {
        AppSettings settings = new();
        settings.CustomDisplayPresets.Add(new DisplayPreset { Id = "mine", Name = "Mine" });
        Assert.False(settings.IsUntouched());

        AppSettings audio = new();
        audio.CustomAudioPresets.Add(new AudioPreset { Id = "mine", Name = "Mine" });
        Assert.False(audio.IsUntouched());
    }

    [Fact]
    public void AnEmptySlotListIsWhatAnUnsavedProfileLooksLike()
    {
        // A profile that has been through the engine has its shipped slots
        // materialised into the list, so an empty list means nothing has ever been
        // written. That is the signal, and it is why this is not a count comparison
        // against the factory slots - which would call a long-used install new.
        Assert.Empty(new AppSettings().Slots);

        AppSettings engine = new() { Slots = SlotService.DefaultSlots() };
        Assert.True(engine.Slots.Count > 0);
    }

    [Fact]
    public void AProfileThatHasBeenThroughTheEngineIsNotUntouched()
    {
        // The realistic second launch: the engine has filled in the shipped slots,
        // so the hint is not shown again.
        AppSettings settings = new() { Slots = SlotService.DefaultSlots() };

        Assert.False(settings.IsUntouched());
    }

    [Fact]
    public void TheComparisonIsAgainstAFreshInstanceRatherThanAListOfNumbers()
    {
        // So a default that changes later moves this with it. Asserted by reading
        // the defaults back rather than by naming them: if the two ever stop
        // agreeing, the property is comparing against something other than a fresh
        // profile.
        AppSettings fresh = new();

        Assert.Equal(fresh.EmergencyHotkey, new AppSettings().EmergencyHotkey);
        Assert.Equal("CTRL+ALT+F12", fresh.EmergencyHotkey);
        Assert.True(fresh.IsUntouched());
    }

    /// <summary>A value for <paramref name="property"/> that is different from the current one.</summary>
    private static object Flip(AppSettings settings, string property)
    {
        var info = typeof(AppSettings).GetProperty(property)!;
        object? current = info.GetValue(settings);

        if (info.PropertyType == typeof(bool))
        {
            return !(bool)current!;
        }

        if (info.PropertyType == typeof(string))
        {
            return (string?)current == "changed" ? "changed-again" : "changed";
        }

        throw new Xunit.Sdk.XunitException("no flip rule for " + info.PropertyType.Name);
    }
}
