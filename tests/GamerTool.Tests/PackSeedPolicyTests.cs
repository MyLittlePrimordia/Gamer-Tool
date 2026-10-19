using System.Collections.Generic;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// How the crosshair pack's starter pictures are kept current: a corrected picture
/// reaches people who have the folder, and nothing the user did is ever undone.
/// </summary>
public class PackSeedPolicyTests
{
    private const string Old = "AAAA";
    private const string New = "BBBB";
    private const string Edited = "CCCC";

    [Fact]
    public void A_picture_that_was_never_written_is_written()
    {
        Assert.Equal(SeedAction.Write, PackSeedPolicy.Decide(false, false, null, null, New));
    }

    [Fact]
    public void A_picture_the_user_deleted_stays_deleted()
    {
        Assert.Equal(SeedAction.Leave, PackSeedPolicy.Decide(false, true, Old, null, New));
    }

    [Fact]
    public void An_untouched_picture_is_replaced_by_the_corrected_one()
    {
        Assert.Equal(SeedAction.Write, PackSeedPolicy.Decide(true, true, Old, Old, New));
    }

    [Fact]
    public void A_picture_the_user_edited_is_never_replaced()
    {
        Assert.Equal(SeedAction.Leave, PackSeedPolicy.Decide(true, true, Old, Edited, New));
    }

    [Fact]
    public void An_up_to_date_picture_is_left_alone()
    {
        Assert.Equal(SeedAction.Leave, PackSeedPolicy.Decide(true, true, New, New, New));
    }

    [Fact]
    public void A_picture_that_was_there_before_the_marker_knew_it_is_the_users()
    {
        // An empty entry is how the first sight of a differing file is remembered.
        Assert.Equal(SeedAction.Leave, PackSeedPolicy.Decide(true, true, string.Empty, Old, New));
        Assert.Equal(SeedAction.Leave, PackSeedPolicy.Decide(true, false, null, Old, New));
    }

    [Fact]
    public void Writing_records_what_was_written()
    {
        Assert.Equal(New, PackSeedPolicy.Record(SeedAction.Write, true, true, Old, Old, New));
        Assert.Equal(New, PackSeedPolicy.Record(SeedAction.Write, false, false, null, null, New));
    }

    [Fact]
    public void Leaving_a_known_picture_keeps_its_record()
    {
        Assert.Equal(Old, PackSeedPolicy.Record(SeedAction.Leave, true, true, Old, Edited, New));
        Assert.Equal(Old, PackSeedPolicy.Record(SeedAction.Leave, false, true, Old, null, New));
    }

    [Fact]
    public void A_file_that_already_matches_what_ships_is_recorded_so_later_fixes_reach_it()
    {
        Assert.Equal(New, PackSeedPolicy.Record(SeedAction.Leave, true, false, null, New, New));
    }

    [Fact]
    public void A_file_that_differs_and_was_never_recorded_is_marked_as_the_users()
    {
        Assert.Equal(string.Empty, PackSeedPolicy.Record(SeedAction.Leave, true, false, null, Edited, New));
    }

    [Fact]
    public void The_marker_survives_a_round_trip_and_is_stable()
    {
        var marker = new Dictionary<string, string> { ["b.png"] = New, ["a.png"] = string.Empty };

        string text = PackSeedPolicy.SerializeMarker(marker);
        Assert.Equal(text, PackSeedPolicy.SerializeMarker(PackSeedPolicy.ParseMarker(text)));
        Assert.Equal(New, PackSeedPolicy.ParseMarker(text)["b.png"]);
        Assert.Equal(string.Empty, PackSeedPolicy.ParseMarker(text)["a.png"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3F2A9C0D7E1B")]          // the old marker: one hash of the names
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void A_marker_that_is_not_the_new_format_means_no_history(string? text)
    {
        Assert.Empty(PackSeedPolicy.ParseMarker(text));
    }

    [Fact]
    public void The_same_bytes_always_give_the_same_fingerprint_and_different_bytes_do_not()
    {
        Assert.Equal(PackSeedPolicy.Hash(new byte[] { 1, 2, 3 }), PackSeedPolicy.Hash(new byte[] { 1, 2, 3 }));
        Assert.False(PackSeedPolicy.Hash(new byte[] { 1, 2, 3 }) == PackSeedPolicy.Hash(new byte[] { 1, 2, 4 }));
    }
}
