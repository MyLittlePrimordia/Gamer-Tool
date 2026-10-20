using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// The decisions behind moving Windows' default audio endpoints.
/// <para>
/// The switch itself cannot be exercised without an audio stack, so these pin
/// down the part that can be: which roles a slot asks for, when FxSound makes a
/// playback route a bypass rather than a convenience, and whether the COM
/// wrapper is still pointing at the entry point Windows actually exposes.
/// </para>
/// </summary>
public class WindowsAudioRoutingTests
{
    private const string HeadsetId = "render-headset";
    private const string SpeakersId = "render-speakers";
    private const string MicId = "capture-mic";

    private static AudioDeviceInfo Device(string id, string name) => new()
    {
        Id = id,
        Name = name,
    };

    private static HotkeySlot Slot() => new()
    {
        Id = "slot_arena",
        Name = "Arena",
        WindowsPlaybackDeviceId = HeadsetId,
        WindowsRecordingDeviceId = MicId,
    };

    [Fact]
    public void PlaybackAndMicrophoneAreBothPlanned()
    {
        WindowsAudioRoutePlan plan = WindowsAudioRoutePlan.Build(
            Slot(),
            new[] { Device(HeadsetId, "Headset") },
            new[] { Device(MicId, "Desk mic") },
            fxSoundRunning: false);

        Assert.True(plan.HasRoutes);
        Assert.Equal(2, plan.Routes.Count);
        Assert.Contains(plan.Routes, r => r.Flow == WindowsAudioFlow.Playback && r.DeviceId == HeadsetId);
        Assert.Contains(plan.Routes, r => r.Flow == WindowsAudioFlow.Recording && r.DeviceId == MicId);
        Assert.Equal("playback + microphone", plan.Summary);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void FxSoundRunningSkipsPlaybackUnlessTheSlotAllowsIt()
    {
        HotkeySlot slot = Slot();

        WindowsAudioRoutePlan quiet = WindowsAudioRoutePlan.Build(
            slot,
            new[] { Device(HeadsetId, "Headset") },
            new[] { Device(MicId, "Desk mic") },
            fxSoundRunning: true);

        Assert.Equal(WindowsAudioFlow.Recording, Assert.Single(quiet.Routes).Flow);
        Assert.Contains(quiet.Notes, n => n.Contains("FxSound", StringComparison.Ordinal));

        slot.WindowsAudioBypassFxSound = true;

        WindowsAudioRoutePlan allowed = WindowsAudioRoutePlan.Build(
            slot,
            new[] { Device(HeadsetId, "Headset") },
            new[] { Device(MicId, "Desk mic") },
            fxSoundRunning: true);

        Assert.Equal(2, allowed.Routes.Count);
        Assert.Contains(allowed.Routes, r => r.Flow == WindowsAudioFlow.Playback);
        Assert.Empty(allowed.Notes);
    }

    [Fact]
    public void TheFxSoundEndpointIsNotABypass()
    {
        // Routing to FxSound keeps the equalizer in the chain, so it does not
        // need the explicit permission that a physical endpoint does.
        HotkeySlot slot = Slot();
        slot.WindowsRecordingDeviceId = string.Empty;

        WindowsAudioRoutePlan plan = WindowsAudioRoutePlan.Build(
            slot,
            new[] { Device(HeadsetId, "Speakers (FxSound Audio Router)") },
            Array.Empty<AudioDeviceInfo>(),
            fxSoundRunning: true);

        Assert.Contains(plan.Routes, r => r.Flow == WindowsAudioFlow.Playback);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void CommunicationsFlagsAddTheVoiceChatRoles()
    {
        HotkeySlot slot = Slot();
        slot.WindowsPlaybackCommunications = true;
        slot.WindowsRecordingCommunications = true;

        WindowsAudioRoutePlan plan = WindowsAudioRoutePlan.Build(
            slot,
            new[] { Device(HeadsetId, "Headset") },
            new[] { Device(MicId, "Desk mic") },
            fxSoundRunning: false);

        Assert.Equal(4, plan.Routes.Count);
        Assert.Contains(plan.Routes, r => r.Flow == WindowsAudioFlow.PlaybackCommunications);
        Assert.Contains(plan.Routes, r => r.Flow == WindowsAudioFlow.RecordingCommunications);
        Assert.Equal("playback + microphone + voice chat", plan.Summary);
    }

    [Fact]
    public void ADeviceThatIsNotConnectedIsReportedAndSkipped()
    {
        HotkeySlot slot = Slot();
        slot.WindowsPlaybackDeviceId = SpeakersId;

        WindowsAudioRoutePlan plan = WindowsAudioRoutePlan.Build(
            slot,
            new[] { Device(HeadsetId, "Headset") },
            new[] { Device(MicId, "Desk mic") },
            fxSoundRunning: false);

        Assert.Equal(WindowsAudioFlow.Recording, Assert.Single(plan.Routes).Flow);
        Assert.Contains(plan.Notes, n => n.Contains("Playback device", StringComparison.Ordinal));
        Assert.Equal("microphone", plan.Summary);
    }

    [Fact]
    public void ARouteOnlySlotCountsAsWork()
    {
        HotkeySlot slot = new()
        {
            Id = "slot_route",
            Name = "Route only",
            WindowsRecordingDeviceId = MicId,
        };

        Assert.True(slot.HasWindowsAudioRouting);
        Assert.True(slot.HasWork);
        Assert.Equal("ROUTE ONLY", slot.WorkText);
    }

    [Fact]
    public void CopyCarriesTheRouteAndItsExplicitBypass()
    {
        HotkeySlot original = Slot();
        original.WindowsPlaybackCommunications = true;
        original.WindowsRecordingCommunications = true;
        original.WindowsAudioBypassFxSound = true;

        HotkeySlot copy = original.Copy();

        Assert.Equal(original.WindowsPlaybackDeviceId, copy.WindowsPlaybackDeviceId);
        Assert.Equal(original.WindowsRecordingDeviceId, copy.WindowsRecordingDeviceId);
        Assert.True(copy.WindowsPlaybackCommunications);
        Assert.True(copy.WindowsRecordingCommunications);
        Assert.True(copy.WindowsAudioBypassFxSound);
    }

    [Fact]
    public void DuplicateCarriesTheRoute()
    {
        HotkeySlot original = Slot();
        original.WindowsAudioBypassFxSound = true;

        HotkeySlot copy = SlotService.Duplicate(original, 2);

        Assert.Equal(original.WindowsPlaybackDeviceId, copy.WindowsPlaybackDeviceId);
        Assert.Equal(original.WindowsRecordingDeviceId, copy.WindowsRecordingDeviceId);
        Assert.True(copy.WindowsAudioBypassFxSound);
    }

    [Fact]
    public void RouteMemoryKeepsTheFirstBeforeAndTheLastAfter()
    {
        WindowsAudioRouteMemory memory = new();

        memory.Remember(WindowsAudioFlow.Playback, "speakers", "headset-a");
        memory.Remember(WindowsAudioFlow.Playback, "headset-a", "headset-b");

        Assert.True(memory.ShouldRestore(WindowsAudioFlow.Playback, "headset-b", out string before));
        Assert.Equal("speakers", before);
    }

    [Fact]
    public void RouteMemoryLeavesAHandChangedDefaultAlone()
    {
        WindowsAudioRouteMemory memory = new();
        memory.Remember(WindowsAudioFlow.Playback, "speakers", "headset-a");

        Assert.False(memory.ShouldRestore(WindowsAudioFlow.Playback, "desk-speakers", out _));
    }

    [Fact]
    public void RouteMemoryDoesNotRecordAChangeThatDidNotHappen()
    {
        WindowsAudioRouteMemory memory = new();
        memory.Remember(WindowsAudioFlow.Playback, "headset-a", "headset-a");

        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void RouteMemoryCanBeForgottenAfterTheRestoreAttempt()
    {
        WindowsAudioRouteMemory memory = new();
        memory.Remember(WindowsAudioFlow.Recording, "desk-mic", "headset-mic");

        memory.Forget(WindowsAudioFlow.Recording);

        Assert.Equal(0, memory.Count);
        Assert.False(memory.ShouldRestore(WindowsAudioFlow.Recording, "headset-mic", out _));
    }

    [Fact]
    public void PolicyConfigKeepsSetDefaultEndpointAtTheWindowsVtableSlot()
    {
        Type? policy = typeof(WindowsAudioRouting).GetNestedType(
            "IPolicyConfig",
            BindingFlags.NonPublic);

        Assert.NotNull(policy);

        MethodInfo[] methods = policy!
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .OrderBy(method => method.MetadataToken)
            .ToArray();

        int index = Array.FindIndex(methods, method => method.Name == "SetDefaultEndpoint");

        // Everything before the setter is a declaration of the vtable, not an
        // API this app calls. If one is removed, this is the assertion that
        // notices before Windows starts being asked to set the wrong thing.
        Assert.Equal(10, index);
        Assert.Equal(11, methods.Length);
    }

    [Fact]
    public void PolicyConfigUsesTheRegisteredInterfaceAndClass()
    {
        Type? policy = typeof(WindowsAudioRouting).GetNestedType(
            "IPolicyConfig",
            BindingFlags.NonPublic);
        Type? client = typeof(WindowsAudioRouting).GetNestedType(
            "PolicyConfigClient",
            BindingFlags.NonPublic);

        Assert.NotNull(policy);
        Assert.NotNull(client);

        Assert.Equal(
            "F8679F50-850A-41CF-9C72-430F290290C8",
            policy!.GetCustomAttribute<GuidAttribute>()!.Value.ToUpperInvariant());
        Assert.Equal(
            "870AF99C-171D-4F9E-AF0D-E63DF40C2BC9",
            client!.GetCustomAttribute<GuidAttribute>()!.Value.ToUpperInvariant());
    }
}
