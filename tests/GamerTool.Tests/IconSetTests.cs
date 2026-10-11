using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using GamerTool.UI;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// Every glyph in the icon set is a real, drawable shape on the shared canvas.
/// </summary>
/// <remarks>
/// The set exists so the buttons share one weight and one optical size, and the
/// failure this guards against is a path that does not parse or that parses to
/// nothing - a button with an invisible glyph, which looks like a broken control
/// rather than a broken string. Nothing could catch it before: an <c>IconButton</c>
/// draws whatever geometry it is handed, so a malformed path is a silent blank at
/// run time and never a build error.
/// <para>
/// Reflected over the properties rather than listed, so a glyph added later is
/// covered by having been added rather than by somebody remembering this file.
/// </para>
/// </remarks>
[Collection("wpf")]
public class IconSetTests
{
    /// <summary>The canvas every glyph is drawn on, from the class's own documentation.</summary>
    private const double Canvas = 24.0;

    private static IEnumerable<(string Name, Geometry Geometry)> All()
    {
        foreach (PropertyInfo p in typeof(Icons).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (p.PropertyType == typeof(Geometry) && p.GetValue(null) is Geometry g)
            {
                yield return (p.Name, g);
            }
        }
    }

    [Fact]
    public void ThereIsAnIconSetToCheck()
    {
        // A reflection sweep that found nothing would pass every test below it.
        Assert.NotEmpty(All());
    }

    [Fact]
    public void EveryGlyphDrawsSomething()
    {
        List<string> blank = new();

        foreach ((string name, Geometry geometry) in All())
        {
            if (geometry.IsEmpty() || geometry.Bounds.Width <= 0 || geometry.Bounds.Height <= 0)
            {
                blank.Add(name);
            }
        }

        Assert.True(blank.Count == 0, "these glyphs draw nothing: " + string.Join(", ", blank));
    }

    [Fact]
    public void EveryGlyphSitsInsideTheCanvas()
    {
        // Outside the 24 unit box means it is clipped or drawn off-centre against
        // every other button beside it.
        List<string> stray = new();

        foreach ((string name, Geometry geometry) in All())
        {
            Rect b = geometry.Bounds;

            if (b.Left < -0.5 || b.Top < -0.5 || b.Right > Canvas + 0.5 || b.Bottom > Canvas + 0.5)
            {
                stray.Add($"{name} ({b.Left:0.#},{b.Top:0.#})-({b.Right:0.#},{b.Bottom:0.#})");
            }
        }

        Assert.True(stray.Count == 0, "these glyphs fall outside the canvas: " + string.Join(", ", stray));
    }

    [Fact]
    public void EveryGlyphIsFrozen()
    {
        // These are shared across every button that uses them and are read on the
        // render thread. An unfrozen Freezable is a cross-thread hazard and a
        // per-use copy; the class's own helper exists to freeze them.
        List<string> unfrozen = new();

        foreach ((string name, Geometry geometry) in All())
        {
            if (geometry.CanFreeze && !geometry.IsFrozen)
            {
                unfrozen.Add(name);
            }
        }

        Assert.True(unfrozen.Count == 0, "these glyphs are not frozen: " + string.Join(", ", unfrozen));
    }

    [Fact]
    public void TheCrosshairIsDistinguishableFromThePlus()
    {
        // They sit two buttons apart on the Hotkeys header and mean different
        // things - one adds an empty slot, the other binds the game you are in. Two
        // crosses in a row would be the one thing this control could not be.
        Assert.False(
            Icons.Crosshair.Bounds == Icons.Plus.Bounds,
            "the crosshair and the plus now occupy the same box");

        Assert.True(Icons.Crosshair.Bounds.Width > Icons.Plus.Bounds.Width);
    }
}
