using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerTool.Models;

namespace GamerTool.Services;

public sealed class DisplayPreview
{
    private const int MaxWidth = 900;

    private readonly byte[]? _day;
    private readonly byte[]? _night;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;

    public DisplayPreview()
    {
        byte[]? day = LoadPixels("day.jpg", out _width, out _height, out _stride);
        _day = day;
        _night = day is null ? null : LoadPixels("night.jpg", out _, out _, out _);
    }

    public bool Ready => _day is not null;

    public BitmapSource? Render(DisplayPreset preset, bool night)
    {
        byte[]? source = night ? _night ?? _day : _day;
        if (source is null || _width <= 0 || _height <= 0)
        {
            return null;
        }

        byte[] lut = DisplayService.BuildPreviewLut(preset);
        byte[] pixels = new byte[source.Length];
        Buffer.BlockCopy(source, 0, pixels, 0, source.Length);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i];
            int g = pixels[i + 1];
            int r = pixels[i + 2];
            pixels[i] = lut[(b * 3) + 2];
            pixels[i + 1] = lut[(g * 3) + 1];
            pixels[i + 2] = lut[(r * 3) + 0];
        }

        return BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, pixels, _stride);
    }

    private static byte[]? LoadPixels(string fileName, out int width, out int height, out int stride)
    {
        width = 0;
        height = 0;
        stride = 0;

        try
        {
            byte[] raw = ReadAsset(fileName);
            if (raw.Length == 0)
            {
                return null;
            }

            BitmapSource source = BitmapDecoder.Create(
                new MemoryStream(raw),
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.OnLoad).Frames[0];

            if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
            {
                return null;
            }

            int targetWidth = Math.Min(source.PixelWidth, MaxWidth);
            int targetHeight = (int)Math.Round(source.PixelHeight * (targetWidth / (double)source.PixelWidth));
            if (targetHeight < 1)
            {
                targetHeight = 1;
            }

            TransformedBitmap scaled = new(source, new ScaleTransform(targetWidth / (double)source.PixelWidth, targetHeight / (double)source.PixelHeight));
            FormatConvertedBitmap flat = new(scaled, PixelFormats.Bgra32, null, 0);
            int bufferStride = flat.PixelWidth * 4;
            byte[] buffer = new byte[bufferStride * flat.PixelHeight];
            flat.CopyPixels(buffer, bufferStride, 0);

            width = flat.PixelWidth;
            height = flat.PixelHeight;
            stride = bufferStride;
            return buffer;
        }
        catch (Exception ex)
        {
            TraceLog.Write("PREVIEW IMAGE", ex);
            return null;
        }
    }

    public static byte[] ReadAsset(string fileName)
    {
        Assembly assembly = typeof(DisplayPreview).Assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using MemoryStream copy = new();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        return Array.Empty<byte>();
    }
}
