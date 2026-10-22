using System;
using System.Linq;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>The once-a-day settings copies: how they are named, ordered and pruned.</summary>
public class SettingsSnapshotsTests
{
    [Fact]
    public void A_day_has_one_sortable_name()
        => Assert.Equal("settings.json.day-20261019.bak", SettingsSnapshots.NameFor(new DateTime(2026, 10, 19, 23, 59, 0)));

    [Fact]
    public void Newest_comes_first_across_a_month_and_year_boundary()
    {
        string[] names = { "x/settings.json.day-20261231.bak", "x/settings.json.day-20270101.bak", "x/settings.json.day-20261101.bak" };
        Assert.Equal(
            new[] { "x/settings.json.day-20270101.bak", "x/settings.json.day-20261231.bak", "x/settings.json.day-20261101.bak" },
            SettingsSnapshots.Newest(names));
    }

    [Fact]
    public void Only_the_newest_five_survive()
    {
        string[] names = Enumerable.Range(1, 8).Select(d => SettingsSnapshots.NameFor(new DateTime(2026, 10, d))).ToArray();
        Assert.Equal(
            new[] { SettingsSnapshots.NameFor(new DateTime(2026, 10, 3)), SettingsSnapshots.NameFor(new DateTime(2026, 10, 2)), SettingsSnapshots.NameFor(new DateTime(2026, 10, 1)) },
            SettingsSnapshots.Surplus(names));
    }

    [Fact]
    public void Nothing_is_pruned_at_or_under_the_limit()
        => Assert.Empty(SettingsSnapshots.Surplus(new[] { "a", "b", "c" }));

    [Fact]
    public void A_days_copy_knows_which_day_it_is()
        => Assert.Equal(
            new DateTime(2026, 10, 19),
            SettingsSnapshots.DayOf(@"C:\x\settings.json.day-20261019.bak"));

    [Fact]
    public void A_file_that_is_not_a_daily_copy_has_no_day()
    {
        Assert.Null(SettingsSnapshots.DayOf(@"C:\x\settings.json"));
        Assert.Null(SettingsSnapshots.DayOf(@"C:\x\settings.json.bak"));
        Assert.Null(SettingsSnapshots.DayOf(@"C:\x\settings.json.day-nope.bak"));
    }
}
