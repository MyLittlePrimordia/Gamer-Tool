using System;
using System.IO;
using System.Linq;
using GamerTool.Models;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// A factory reset is the one action that costs the user their work in bulk, so what
/// is tested here is the promise the dialog makes: it asks first, it changes nothing
/// until it is confirmed, and the settings it is about to replace have somewhere of
/// their own to be written to.
/// <para>
/// The confirmed half of the reset is not driven here. It re-registers the profile's
/// hotkeys, which means claiming real system-wide keys from a test process, and one
/// test that types into the user's desktop is worth less than the promise above.
/// </para>
/// </summary>
[Collection("wpf")]
public sealed class FactoryResetTests
{
    [Fact]
    public void The_reset_asks_before_it_changes_anything()
    {
        WindowHarness.Run(MainWindowTabs.None, h =>
        {
            string before = h.Settings.CrosshairHotkey;
            int slots = h.Settings.Slots.Count;

            h.Invoke("OnFactoryResetClick", h.Window, new System.Windows.RoutedEventArgs());

            Assert.Equal(
                System.Windows.Visibility.Visible,
                h.Read<System.Windows.FrameworkElement>("_modalLayer").Visibility);
            Assert.Equal("RESET EVERYTHING?", h.Read<System.Windows.Controls.TextBlock>("_modalTitle").Text);

            // It names the way back, because an undo that nobody knows about is not one.
            string warning = h.Read<System.Windows.Controls.TextBlock>("_modalWarningText").Text;
            Assert.Contains("backup file", warning);
            Assert.Contains("Restore", warning);

            // Nothing has moved: the dialog is a question.
            Assert.Equal(before, h.Settings.CrosshairHotkey);
            Assert.Equal(slots, h.Settings.Slots.Count);
        });
    }

    [Fact]
    public void Reset_keeps_each_copy_under_its_own_name()
    {
        string folder = Path.Combine(Path.GetTempPath(), "gamertool-reset-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);

        try
        {
            DateTime first = new(2026, 10, 22, 19, 4, 5, DateTimeKind.Local);
            string a = FactoryReset.RescuePath(folder, first);
            string b = FactoryReset.RescuePath(folder, first.AddSeconds(1));

            Assert.True(Path.GetDirectoryName(a) == folder, "the rescue must sit beside the profile");
            Assert.True(a != b, "two resets must not share a rescue file");
            Assert.StartsWith(FactoryReset.Prefix, Path.GetFileName(a));
            Assert.Contains("2026-10-22-190405", Path.GetFileName(a));
            Assert.EndsWith(".json", a);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not what the test is about.
            }
        }
    }

    [Fact]
    public void A_reset_lands_on_the_settings_a_first_launch_starts_from()
    {
        string folder = Path.Combine(Path.GetTempPath(), "gamertool-reset-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            ProfileManager profiles = new(folder);
            AppSettings reset = profiles.Defaults();
            AppSettings first = new ProfileManager(Path.Combine(folder, "first-launch")).Load();

            // The same thing a first launch is given, not merely "an AppSettings": the
            // loader normalises, and a reset that skipped that step could hand the app
            // a profile it has never been prepared for.
            Assert.Equal(first.Slots.Count, reset.Slots.Count);
            Assert.Empty(reset.CustomDisplayPresets);
            Assert.Empty(reset.CustomAudioPresets);
            Assert.Empty(reset.CustomCrosshairs);
            Assert.Equal(first.EmergencyHotkey, reset.EmergencyHotkey);
            Assert.Equal(first.BlueLightFilter, reset.BlueLightFilter);
            Assert.DoesNotContain(reset.Slots, s => !string.IsNullOrEmpty(s.AppExePath));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
