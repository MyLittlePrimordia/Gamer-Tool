using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Reading Steam's own configuration files.
/// <para>
/// Two shapes, and the code serves only one of them.
/// <c>appmanifest_*.acf</c> puts its fields at the top level. <c>libraryfolders.vdf</c>
/// wraps every entry in a numbered object, so its paths sit two levels down. A
/// parser that reads the top level and steps over anything nested - which is a
/// reasonable thing to do, because the manifest is flat and building a tree nobody
/// looks at is wasted work - reads the manifest perfectly and reads the library
/// file as an empty object.
/// </para>
/// <para>
/// Nothing shows it. The manifest parse succeeds, so the scan looks like it
/// worked, and the only consequence is that every game outside the Steam root is
/// missing from the picker with nothing on screen to say why.
/// </para>
/// </summary>
public class SteamLibraryFileTests
{
    /// <summary>The real shape of a libraryfolders.vdf with two secondary libraries.</summary>
    private const string LibraryFoldersVdf = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		""
        		"contentid"		"1234567890123456789"
        		"totalsize"		"0"
        		"time_last_update_verified"		"1700000000"
        		"apps"
        		{
        			"228980"		"5000000000"
        		}
        	}
        	"1"
        	{
        		"path"		"E:\\Games"
        		"label"		""
        		"contentid"		"9876543210987654321"
        		"totalsize"		"0"
        		"time_last_update_verified"		"1700000001"
        		"apps"
        		{
        			"620"		"25000000000"
        		}
        	}
        }
        """;

    /// <summary>The real shape of an appmanifest_*.acf, which is flat.</summary>
    private const string AppManifest = """
        "AppState"
        {
        	"appid"		"228980"
        	"Universe"		"1"
        	"name"		"Half-Life 2"
        	"StateFlags"		"4"
        	"installdir"		"hl2"
        }
        """;

    [Fact]
    public void The_paths_in_a_library_file_are_found()
    {
        // The whole point. These are on the drives this file exists to record,
        // so finding only the Steam root means every game installed anywhere else
        // is invisible.
        var paths = Vdf.ParseAllValues(LibraryFoldersVdf, "path");

        Assert.Contains("D:\\SteamLibrary", paths);
        Assert.Contains("E:\\Games", paths);
    }

    [Fact]
    public void Paths_are_found_however_deeply_they_are_nested()
    {
        // Depth is not the reason it should be found, but a file that nests one
        // level deeper must not be what finally works.
        var paths = Vdf.ParseAllValues(
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"nested\"\n\t\t{\n\t\t\t\"path\"\t\t\"F:\\Deep\"\n\t\t}\n\t}\n}\n",
            "path");

        Assert.Equal(new[] { "F:\\Deep" }, paths);
    }

    [Fact]
    public void A_flat_file_still_reads_through_the_ordinary_path()
    {
        // The manifest must keep working, because it is the shape the existing
        // parser was written for and the one that actually gets parsed today.
        var sections = Vdf.ParseSections(AppManifest);

        Assert.Equal("228980", sections["AppState"]["appid"]);
        Assert.Equal("hl2", sections["AppState"]["installdir"]);
        Assert.Equal("Half-Life 2", sections["AppState"]["name"]);
    }

    [Fact]
    public void The_two_shapes_do_not_confuse_each_other()
    {
        // A manifest has no nested "path" key, so the depth-walking search must
        // not invent one.
        Assert.Empty(Vdf.ParseAllValues(AppManifest, "path"));
    }

    [Fact]
    public void A_library_file_that_names_no_libraries_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(Vdf.ParseAllValues("\"libraryfolders\"\n{\n}\n", "path"));
        Assert.Empty(Vdf.ParseAllValues(string.Empty, "path"));
        Assert.Empty(Vdf.ParseAllValues("}{ nonsense {", "path"));
    }

    [Fact]
    public void A_runaway_nesting_is_refused_rather_than_overflowing_the_stack()
    {
        // A stack overflow cannot be caught, so the process dies with no log line
        // and no emergency reset - which for this app means a gamma ramp left
        // applied to the screen.
        string deep = "\"libraryfolders\"\n" + string.Concat(Enumerable.Repeat("{\n\"k\"\n", 200));
        deep += "\"path\"\t\"X:\\\\TooDeep\"\n" + string.Concat(Enumerable.Repeat("}\n", 200));

        var paths = Vdf.ParseAllValues(deep, "path");

        Assert.Empty(paths);
    }
}
