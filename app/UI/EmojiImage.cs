using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Services;
using Image = System.Windows.Controls.Image;

namespace GamerTool.UI;

/// <summary>
/// One colour emoji, drawn from a baked bitmap.
///
/// WPF cannot render colour emoji at all: Segoe UI Emoji is a COLR/CPAL font and
/// WPF's text stack ignores those tables, painting every glyph in the foreground
/// colour instead. That was checked rather than assumed, by drawing the same glyph
/// four ways, with a foreground, with an inherited one, with none and with ideal
/// formatting, and all four came out as the same outline-only shape.
///
/// So the glyphs are rendered once by the browser's own colour text stack and
/// baked into Assets\emoji as 64 pixel PNGs. Each one keeps the same cell and the
/// same optical size as the rest, so a padlock and a gamepad sit on one baseline
/// in the nav bar instead of drifting apart.
/// </summary>
public sealed class EmojiImage : Image
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(EmojiImage),
        new PropertyMetadata(string.Empty, OnGlyphChanged));

    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public EmojiImage()
    {
        // Emoji carry their own colour, so they are never dimmed with the rest of
        // a disabled row and they opt out of the greyscale nudge some styles apply.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Stretch = System.Windows.Media.Stretch.Uniform;
    }

    /// <summary>Asset name under Assets\emoji, without the extension.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    private static void OnGlyphChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is EmojiImage image)
        {
            image.Source = Load(e.NewValue as string);
        }
    }

    private static BitmapSource? Load(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string key = name.Trim();
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out BitmapSource? cached))
            {
                return cached;
            }
        }

        BitmapSource? made = Read(key);
        lock (Gate)
        {
            Cache[key] = made!;
        }

        return made;
    }

    private static BitmapSource? Read(string name)
    {
        try
        {
            Assembly assembly = typeof(EmojiImage).Assembly;
            string suffix = ".emoji." + name.ToLowerInvariant() + ".png";

            // Matched on the tail rather than built from the namespace, so a change
            // to the root namespace or the folder casing cannot silently break it.
            string? resource = null;
            foreach (string candidate in assembly.GetManifestResourceNames())
            {
                if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    resource = candidate;
                    break;
                }
            }

            if (resource is null)
            {
                return null;
            }

            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                return null;
            }

            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            TraceLog.Write("EMOJI " + name, ex);
            return null;
        }
    }
}
