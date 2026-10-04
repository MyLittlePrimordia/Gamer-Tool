using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Reading a published version out of what winget actually prints.
/// <para>
/// This exists because the check was silently broken and the test suite did not
/// notice. The parser looked for a line containing nothing but digits and dots,
/// on the assumption that winget prints the number on a line of its own. It does
/// not - it prints a labelled field - so every check came back empty, and the
/// caller had been reading empty as "nothing to report". A user pressing the
/// button was told the app had looked and found nothing, having never looked.
/// </para>
/// <para>
/// The winget output quoted below is verbatim, captured from a real
/// <c>winget show FxSound.FxSound</c> on 2026-10-03. A hand-written sample would
/// have reproduced whatever the author believed winget printed, which is the
/// mistake being guarded against.
/// </para>
/// </summary>
public class FxUpdateParseTests
{
    /// <summary>Verbatim winget output. Do not tidy this.</summary>
    private const string RealWingetShow =
        "Found FxSound [FxSound.FxSound]\n"
        + "Version: 1.2.15.0\n"
        + "Publisher: FxSound LLC\n"
        + "Publisher Url: https://www.fxsound.com/\n"
        + "Publisher Support Url: https://github.com/fxsound2/fxsound-app/issues\n"
        + "Author: FxSound LLC\n"
        + "Description: Free open-source software to boost sound quality, volume, and bass. Including an equalizer, effects, and presets for customized audio.\n"
        + "Homepage: https://www.fxsound.com/download\n";

    [Fact]
    public void The_version_is_read_out_of_real_winget_output()
    {
        // The whole bug in one assertion: this returned empty, which the caller
        // could not tell apart from "up to date".
        Assert.Equal("1.2.15.0", SetupService.ParsePublishedVersion(RealWingetShow));
    }

    [Fact]
    public void A_bare_version_line_is_still_read()
    {
        // The shape the old rule was written for, kept so anything reporting a
        // bare number keeps working.
        Assert.Equal("2.0.1.0", SetupService.ParsePublishedVersion("FxSound\n2.0.1.0\nInstaller\n"));
    }

    [Fact]
    public void A_labelled_version_is_preferred_over_a_bare_one_elsewhere()
    {
        // winget's labelled field is the authoritative one. A bare number further
        // down could be an installer block or a dependency listing.
        Assert.Equal(
            "1.2.15.0",
            SetupService.ParsePublishedVersion("Version: 1.2.15.0\nsome other field\n3.4.5.6\n"));
    }

    [Fact]
    public void A_url_is_never_mistaken_for_a_version()
    {
        // A dotted run inside a URL is not a release number, and the description
        // and homepage lines are exactly where one would turn up.
        Assert.Equal(string.Empty, SetupService.ParsePublishedVersion(
            "Publisher Url: https://www.fxsound.com/1.2.3\nHomepage: https://x.example/4.5.6\n"));
    }

    [Fact]
    public void A_version_field_with_nothing_usable_in_it_reads_as_nothing()
    {
        // The state that used to be indistinguishable from "up to date".
        Assert.Equal(string.Empty, SetupService.ParsePublishedVersion("Version: unknown\nVersion: \n"));
    }

    [Fact]
    public void Empty_and_noise_output_reads_as_nothing()
    {
        Assert.Equal(string.Empty, SetupService.ParsePublishedVersion(string.Empty));
        Assert.Equal(string.Empty, SetupService.ParsePublishedVersion("\n\n   \n"));
        Assert.Equal(string.Empty, SetupService.ParsePublishedVersion("No package found matching the input criteria."));
    }

    [Fact]
    public void A_four_part_and_a_two_part_version_are_both_read()
    {
        Assert.Equal("1.2.15.0", SetupService.ParsePublishedVersion("Version: 1.2.15.0\n"));
        Assert.Equal("1.2", SetupService.ParsePublishedVersion("Version: 1.2\n"));
    }

    [Fact]
    public void The_label_is_matched_with_whatever_spacing_winget_prints()
    {
        // Leading whitespace is trimmed off, and the value is read after the
        // first colon, so an indented or padded field still parses.
        Assert.Equal("1.2.15.0", SetupService.ParsePublishedVersion("   Version:   1.2.15.0  \n"));
    }
}