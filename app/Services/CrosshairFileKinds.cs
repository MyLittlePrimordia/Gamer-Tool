using System;
using System.IO;

namespace GamerTool.Services;

/// <summary>The kinds of picture a crosshair can be made from.</summary>
public enum PictureKind
{
    /// <summary>Not one of ours.</summary>
    None,

    Png,

    /// <summary>Animated or not. A single frame GIF is just a picture.</summary>
    Gif,

    Jpeg,

    Bmp
}

/// <summary>
/// Which files the crosshair tab will take, decided by one list so the file
/// dialog, the importer and the pack browser cannot disagree about it.
/// <para>
/// The importer checks the file's contents against its extension as well, so a
/// renamed file is refused rather than trusted. SVG is left out on purpose: it is
/// a document that can pull in other files, and Windows cannot draw one without
/// shipping a vector engine, which would break the single file, offline promise.
/// </para>
/// </summary>
internal static class CrosshairFileKinds
{
    /// <summary>For the Import button. The first filter is the one shown, so everything is one click away.</summary>
    public const string OpenFilter =
        "Pictures (*.png;*.gif;*.jpg;*.jpeg;*.bmp)|*.png;*.gif;*.jpg;*.jpeg;*.bmp"
        + "|PNG picture (*.png)|*.png"
        + "|Animated GIF (*.gif)|*.gif"
        + "|JPEG picture (*.jpg;*.jpeg)|*.jpg;*.jpeg"
        + "|Bitmap picture (*.bmp)|*.bmp";

    /// <summary>The extensions the pack browser lists, lower case with the dot.</summary>
    public static readonly string[] Extensions = { ".png", ".gif", ".jpg", ".jpeg", ".bmp" };

    public static PictureKind FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PictureKind.None;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => PictureKind.Png,
            ".gif" => PictureKind.Gif,
            ".jpg" or ".jpeg" => PictureKind.Jpeg,
            ".bmp" => PictureKind.Bmp,
            _ => PictureKind.None
        };
    }

    public static bool IsSupported(string? path) => FromPath(path) != PictureKind.None;
}
