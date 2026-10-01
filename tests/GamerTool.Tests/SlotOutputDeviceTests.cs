using System;
using System.Collections.Generic;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Which output a slot's sound actually goes to.
/// <para>
/// A slot can be pointed at a device of its own, so "Footsteps EQ and switch to
/// my headset" is one keypress rather than two. The part that needs care is what
/// happens when the device is not there any more.
/// </para>
/// <para>
/// The engine does not always take a device, and the app has a repair path for
/// when it does not, which works by noticing a disagreement and putting a usable
/// device back. Handing that repair a name which is already gone would be asking
/// it to recover from something the app could have answered before the engine
/// ever saw it. So an unknown name resolves to the app's own setting, and the
/// repair never gets the chance to be needed.
/// </para>
/// </summary>
public class SlotOutputDeviceTests
{
    private static readonly string[] Known = { "Speakers (Realtek Audio)", "Headset (USB Audio)" };

    private static HotkeySlot RoutedTo(string device) => new() { Name = "Gaming", OutputDeviceId = device };

    [Fact]
    public void A_slot_with_no_device_follows_the_app()
    {
        // The default, and the reason the field is empty rather than a copied
        // value: a slot made before a device was chosen keeps tracking the app's
        // setting instead of pinning itself to whatever happened to be selected
        // on the day it was made.
        Assert.Equal(
            "Speakers (Realtek Audio)",
            SlotService.ResolveOutputDevice(RoutedTo(""), Known, "Speakers (Realtek Audio)"));
    }

    [Fact]
    public void A_slot_with_its_own_device_uses_it()
    {
        Assert.Equal(
            "Headset (USB Audio)",
            SlotService.ResolveOutputDevice(RoutedTo("Headset (USB Audio)"), Known, "Speakers (Realtek Audio)"));
    }

    [Fact]
    public void A_device_that_is_no_longer_there_falls_back_to_the_app()
    {
        // The case that decides whether this is safe. The headset was unplugged,
        // or a driver update renamed it, and the slot still remembers the old
        // name. Sending it would be asking the engine for something that is not
        // there and then relying on the repair to notice.
        Assert.Equal(
            "Speakers (Realtek Audio)",
            SlotService.ResolveOutputDevice(RoutedTo("Headset (USB Audio)"), Array.Empty<string>(), "Speakers (Realtek Audio)"));
    }

    [Fact]
    public void A_device_name_is_matched_without_regard_to_capitals()
    {
        // These names come back from a driver, and a hand edited profile is not
        // going to agree with it about capitals.
        Assert.Equal(
            "Headset (USB Audio)",
            SlotService.ResolveOutputDevice(RoutedTo("headset (usb audio)"), Known, "Speakers (Realtek Audio)"));
    }

    [Fact]
    public void A_blank_device_on_the_slot_is_never_a_device_to_match()
    {
        // Whitespace rather than empty: a hand edited file, or a value that went
        // through a trim. It must not be treated as a device that happens to be
        // called nothing.
        Assert.Equal(
            "Speakers (Realtek Audio)",
            SlotService.ResolveOutputDevice(RoutedTo("   "), Known, "Speakers (Realtek Audio)"));
    }

    [Fact]
    public void An_app_with_no_device_set_still_gets_the_slots_own()
    {
        // "System default" is the empty id, so the app's own setting can be
        // empty. A slot pointing somewhere real must still get there.
        Assert.Equal(
            "Headset (USB Audio)",
            SlotService.ResolveOutputDevice(RoutedTo("Headset (USB Audio)"), Known, string.Empty));
    }

    [Fact]
    public void Copying_a_slot_keeps_its_device()
    {
        // The copy is a new slot the user made, so it should arrive the way the
        // one they duplicated does rather than silently reverting to following
        // the app. Everything else in Copy is the same argument.
        HotkeySlot copy = RoutedTo("Headset (USB Audio)").Copy();

        Assert.Equal("Headset (USB Audio)", copy.OutputDeviceId);
    }

    [Fact]
    public void A_new_slot_follows_the_app_by_default()
    {
        HotkeySlot fresh = SlotService.NewSlot(3);

        Assert.Equal(string.Empty, fresh.OutputDeviceId);
    }

    [Fact]
    public void The_shipped_slots_follow_the_app()
    {
        // The built-ins were written before this existed, and a fresh install
        // must behave exactly as it did before the field was added.
        foreach (HotkeySlot slot in SlotService.FactorySlots)
        {
            Assert.Equal(string.Empty, slot.OutputDeviceId);
        }
    }

    [Fact]
    public void A_saved_slot_without_the_field_still_loads()
    {
        // A profile written by an older build has no such key, and a missing one
        // has to mean "follow the app" rather than a null that quietly routes
        // sound nowhere.
        string json = """
        {
          "Schema": 2,
          "Slots": [
            {
              "Id": "abc",
              "Name": "Gaming",
              "BuiltIn": true,
              "DisplayPresetId": "gaming",
              "AudioPresetId": "footsteps",
              "Hotkey": "CTRL+ALT+1",
              "Enabled": true
            }
          ]
        }
        """;

        Models.AppSettings? settings = System.Text.Json.JsonSerializer.Deserialize<Models.AppSettings>(json);

        Assert.NotNull(settings);
        HotkeySlot? loaded = settings!.Slots.FirstOrDefault(s => s.Id == "abc");

        Assert.NotNull(loaded);
        Assert.Equal(string.Empty, loaded!.OutputDeviceId);
    }

    [Fact]
    public void The_field_survives_a_profile_round_trip()
    {
        // The persistence half. A route the user chose and cannot see on the row
        // has exactly one place it can be recorded, and that is the profile.
        HotkeySlot slot = RoutedTo("Headset (USB Audio)");

        string json = System.Text.Json.JsonSerializer.Serialize(slot);
        HotkeySlot? back = System.Text.Json.JsonSerializer.Deserialize<HotkeySlot>(json);

        Assert.NotNull(back);
        Assert.Equal("Headset (USB Audio)", back!.OutputDeviceId);
    }
}
