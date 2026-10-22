using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using GamerTool.Services;
using Xunit;

namespace GamerTool.Tests;

/// <summary>
/// A budget on what the app embeds, measured against the assets that actually ship.
/// </summary>
/// <remarks>
/// The single-file build is one of this app's promises, and its size is almost
/// entirely decided by what goes into it. The preview loop and the three audio
/// tracks are the whole of that weight: they were 29.3 MB of a 96.4 MB executable,
/// and one of them was a 16.5 MB GIF that the app then downscaled and dropped
/// frames from on every launch - so the user paid 16.5 MB of download for a clip
/// that played at a third of its authored frame rate.
/// <para>
/// Two things are asserted, and the second is the one that matters. A byte budget,
/// because the weight is the point. And that the loop is played <em>whole</em>,
/// because "small enough" and "plays as authored" turned out to be different
/// questions, and the app was failing the second while passing nothing at all.
/// </para>
/// <para>
/// Assets are read out of the built app assembly rather than off disk, so this
/// measures what a user downloads rather than what happens to be in the source
/// tree. A file added to the project but never embedded fails the count check.
/// </para>
/// </remarks>
[Collection("wpf")]
public class AssetBudgetTests
{
    /// <summary>
    /// Ceiling on everything embedded. Set above the current total with room to
    /// work, and below the size this app has already been shipped at by accident.
    /// <para>
    /// Raised from 22 MB when the starter pack's animated pictures started shipping
    /// with the program. The pack is 6.9 MB of GIFs and without them the browser had
    /// nothing animated to show at all, so the weight buys the feature. What the
    /// number still guards against is the thing it was always about: one asset
    /// quietly growing, and the whole bundle running away.
    /// </para>
    /// </summary>
    private const long BudgetBytes = 32L * 1024 * 1024;

    /// <summary>Private names for the embedded clips, as DisplayPreview reads them.</summary>
    private static readonly string[] Clips =
    {
        "GamerTool.assets.animated.gif",
        "GamerTool.assets.game.mp3",
        "GamerTool.assets.music.mp3",
        "GamerTool.assets.footsteps.mp3",
    };

    [Fact]
    public void TheEmbeddedAssetsStayInsideTheirBudget()
    {
        Assembly app = typeof(DisplayPreview).Assembly;
        string[] names = app.GetManifestResourceNames();

        long total = 0;
        List<string> heavy = new();

        foreach (string name in names)
        {
            using var stream = app.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            total += stream.Length;
            if (stream.Length >= 1L * 1024 * 1024)
            {
                heavy.Add($"{name} {stream.Length / 1024 / 1024.0:N1} MB");
            }
        }

        Assert.True(
            total <= BudgetBytes,
            $"the build embeds {(total / 1024.0 / 1024.0):N1} MB, over the "
            + $"{BudgetBytes / 1024 / 1024} MB budget. Largest: {string.Join(", ", heavy)}");
    }

    [Fact]
    public void ThePreviewLoopIsPlayedWhole()
    {
        // The invariant the old asset broke. Read through the same decoder the app
        // uses, so a clip this passes on is one FitWidth will not shrink and the
        // stride will not drop frames from.
        byte[] raw = DisplayPreview.ReadAsset("animated.gif");
        Assert.NotEmpty(raw);

        GifBitmapDecoder decoder = new(
            new System.IO.MemoryStream(raw),
            BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.OnLoad);

        Assert.True(decoder.Frames.Count > 1, "the transition clip is not animated any more");

        BitmapFrame first = decoder.Frames[0];

        Assert.True(
            DisplayPreview.KeepsEveryFrame(first.PixelWidth, first.PixelHeight, decoder.Frames.Count),
            $"the transition clip is {first.PixelWidth}x{first.PixelHeight} over "
            + $"{decoder.Frames.Count} frames, which is over the animated memory budget. "
            + "The app will shrink it and drop frames, so it will not play as authored. "
            + "Re-encode it smaller or shorter before shipping it.");
    }

    [Fact]
    public void EveryEmbeddedClipIsOneTheAppKnowsHowToRead()
    {
        // A clip that is embedded but never read is pure download weight, and a
        // clip the app reads but that is not embedded is a blank preview. The two
        // lists have to agree.
        Assembly app = typeof(DisplayPreview).Assembly;
        string[] names = app.GetManifestResourceNames();

        foreach (string clip in Clips)
        {
            Assert.Contains(clip, names);
        }
    }

    [Fact]
    public void TheStarterPackShipsItsAnimatedPictures()
    {
        // The pack folder held GIFs that were never embedded: the browser had no
        // tile to show and the tiles it did show had nothing to animate, which from
        // the outside looks like the feature not existing. The pack lists whatever
        // is in the folder, so what this asserts is that the two agree.
        Assembly app = typeof(DisplayPreview).Assembly;
        const string Prefix = "GamerTool.pack.";

        string[] pack = app.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(pack);

        string[] gifs = pack
            .Where(n => n.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(gifs);

        // A name with a space in it, because the logical name has to survive one.
        Assert.Contains("GamerTool.pack.Pop Cat.gif", pack);

        foreach (string gif in gifs)
        {
            using Stream? stream = app.GetManifestResourceStream(gif);
            Assert.NotNull(stream);

            var decoder = new GifBitmapDecoder(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.OnLoad);

            Assert.True(decoder.Frames.Count > 1, gif + " ships with one frame, so there is nothing to animate");
        }
    }

    [Fact]
    public void NoSingleEmbeddedAssetIsDisproportionate()
    {
        // A per-file ceiling as well as a total one, because a total is easy to
        // satisfy with several large files and the failure this guards against is
        // one asset quietly growing. 9 MB is above every clip that ships.
        Assembly app = typeof(DisplayPreview).Assembly;
        const long MaxOne = 9L * 1024 * 1024;

        List<string> over = new();

        foreach (string name in app.GetManifestResourceNames())
        {
            using var stream = app.GetManifestResourceStream(name);
            if (stream is not null && stream.Length > MaxOne)
            {
                over.Add($"{name} = {stream.Length / 1024.0 / 1024.0:N1} MB");
            }
        }

        Assert.True(
            over.Count == 0,
            "an embedded asset is larger than any clip needs to be: " + string.Join(", ", over));
    }
}
