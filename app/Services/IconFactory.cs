using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace GamerTool.Services;

public static class IconFactory
{
    public const string SourceAsset = "icon.png";

    private static readonly Color Back = Color.FromArgb(255, 21, 24, 33);

    private static readonly Color Edge = Color.FromArgb(255, 45, 51, 63);

    private static readonly Color Teal = Color.FromArgb(255, 45, 212, 191);

    private static readonly Color Blurple = Color.FromArgb(255, 99, 102, 241);

    public static int[] Sizes { get; } = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static Bitmap Render(int size)
    {
        Bitmap bitmap = new(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        float s = size;
        float radius = s * 0.23f;
        float inset = Math.Max(1f, s * 0.02f);

        using (GraphicsPath path = RoundedRect(new RectangleF(inset, inset, s - (inset * 2), s - (inset * 2)), radius))
        using (SolidBrush fill = new SolidBrush(Back))
        using (Pen edge = new Pen(Edge, Math.Max(1f, s * 0.02f)))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }

        float bodyX = s * 0.10f;
        float bodyY = s * 0.33f;
        float bodyW = s * 0.80f;
        float bodyH = s * 0.34f;
        float bodyR = bodyH / 2f;

        using (GraphicsPath body = RoundedRect(new RectangleF(bodyX, bodyY, bodyW, bodyH), bodyR))
        using (SolidBrush bodyBrush = new SolidBrush(Teal))
        {
            g.FillPath(bodyBrush, body);
        }

        float dpadX = s * 0.285f;
        float dpadY = bodyY + (bodyH * 0.50f);
        float arm = s * 0.115f;
        float thick = s * 0.050f;

        using (SolidBrush dpad = new SolidBrush(Back))
        {
            g.FillRectangle(dpad, dpadX - (arm / 2f), dpadY - (thick / 2f), arm, thick);
            g.FillRectangle(dpad, dpadX - (thick / 2f), dpadY - (arm / 2f), thick, arm);
        }

        float buttonR = s * 0.048f;
        using (SolidBrush button = new SolidBrush(Back))
        {
            g.FillEllipse(button, s * 0.675f - buttonR, dpadY - s * 0.060f - buttonR, buttonR * 2, buttonR * 2);
            g.FillEllipse(button, s * 0.755f - buttonR, dpadY + s * 0.060f - buttonR, buttonR * 2, buttonR * 2);
        }

        float dotR = Math.Max(1f, s * 0.020f);
        using (SolidBrush dot = new SolidBrush(Back))
        {
            g.FillEllipse(dot, s * 0.425f - dotR, dpadY - s * 0.050f - dotR, dotR * 2, dotR * 2);
            g.FillEllipse(dot, s * 0.500f - dotR, dpadY + s * 0.050f - dotR, dotR * 2, dotR * 2);
        }

        return bitmap;
    }

    public static System.Drawing.Icon CreateRuntimeIcon(int size)
    {
        byte[]? png = ReadSourcePng();
        if (png is not null && png.Length > 0)
        {
            using Image source = Image.FromStream(new MemoryStream(png));
            using Bitmap scaled = Scale(source, size);
            IntPtr pngHandle = scaled.GetHicon();
            KeepAlive.Add(pngHandle);
            return System.Drawing.Icon.FromHandle(pngHandle);
        }

        using Bitmap bitmap = Render(size);
        IntPtr handle = bitmap.GetHicon();
        KeepAlive.Add(handle);
        return System.Drawing.Icon.FromHandle(handle);
    }

    private static readonly Dictionary<string, BitmapSource?> AppIconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Launcher icon for an executable, cached per path. Returns null when the file
    /// has no icon or cannot be read, so callers can fall back to plain text.
    /// </summary>
    public static BitmapSource? ExtractAppIcon(string exePath, int size = 20)
    {
        if (string.IsNullOrWhiteSpace(exePath) || exePath == SelfExe)
        {
            return null;
        }

        string key = exePath + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (AppIconCache.TryGetValue(key, out BitmapSource? cached))
        {
            return cached;
        }

        BitmapSource? result = null;
        try
        {
            if (File.Exists(exePath))
            {
                using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null)
                {
                    BitmapSource source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();

                    BitmapSource resized = Resize(source, size);
                    resized.Freeze();
                    result = resized;
                }
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("ICON", ex);
        }

        AppIconCache[key] = result;
        return result;
    }

    private static string SelfExe =>
        System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    private static BitmapSource Resize(BitmapSource source, int size)
    {
        if (source.PixelWidth == size && source.PixelHeight == size)
        {
            return source;
        }

        TransformedBitmap scaled = new(
            source,
            new System.Windows.Media.ScaleTransform(
                (double)size / source.PixelWidth,
                (double)size / source.PixelHeight));
        scaled.Freeze();
        return scaled;
    }

    public static BitmapSource LoadWindowIcon()
    {
        byte[]? png = ReadSourcePng();
        if (png is not null && png.Length > 0)
        {
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();

            if (image.PixelWidth > 0)
            {
                double factor = 64.0 / image.PixelWidth;
                TransformedBitmap scaled = new(image, new System.Windows.Media.ScaleTransform(factor, factor));
                scaled.Freeze();
                return scaled;
            }
        }

        using Bitmap fallback = Scale(Image.FromStream(new MemoryStream(FallbackPng())), 64);
        BitmapSource source2 = ToBitmapSource(fallback);
        source2.Freeze();
        return source2;
    }

    private static byte[] FallbackPng()
    {
        using Bitmap generated = Render(64);
        using MemoryStream buffer = new();
        generated.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        int stride = bitmap.Width * 4;
        byte[] pixels = new byte[stride * bitmap.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bitmap.UnlockBits(data);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        BitmapSource source = System.Windows.Media.Imaging.BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        return source;
    }

    public static byte[]? ReadSourcePng()
    {
        try
        {
            Assembly assembly = typeof(IconFactory).Assembly;
            string? name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + SourceAsset, StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(SourceAsset, StringComparison.OrdinalIgnoreCase));

            if (name is null)
            {
                return null;
            }

            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }

            using MemoryStream copy = new();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception ex)
        {
            TraceLog.Write("ICON SOURCE", ex);
            return null;
        }
    }

    private static Bitmap Scale(Image source, int size)
    {
        Bitmap bitmap = new(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, size, size));
        return bitmap;
    }

    private static readonly List<IntPtr> KeepAlive = new();

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float r = Math.Min(radius, (Math.Min(rect.Width, rect.Height) / 2f));
        GraphicsPath path = new();
        path.AddArc(rect.X, rect.Y, r, r, 180, 90);
        path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
        path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static byte[] Build()
    {
        byte[]? png = ReadSourcePng();
        if (png is not null && png.Length > 0)
        {
            using Image source = Image.FromStream(new MemoryStream(png));
            return BuildFromImage(source);
        }

        List<(byte[] Data, bool Png)> images = new();

        foreach (int size in Sizes)
        {
            using Bitmap bitmap = Render(size);
            images.Add((EncodeBmp(bitmap), false));
        }

        return Pack(images);
    }

    public static byte[] BuildFromImage(Image source)
    {
        List<(byte[] Data, bool Png)> images = new();

        foreach (int size in Sizes)
        {
            using Bitmap scaled = Scale(source, size);
            bool png = size >= 128;
            images.Add((png ? EncodePng(scaled) : EncodeBmp(scaled), png));
        }

        return Pack(images);
    }

    private static byte[] Pack(List<(byte[] Data, bool Png)> images)
    {
        using MemoryStream output = new();
        Write(output, (ushort)0, (ushort)1, (ushort)images.Count);
        int offset = 6 + (16 * images.Count);

        for (int i = 0; i < images.Count; i++)
        {
            byte[] data = images[i].Data;
            WriteEntry(output, Sizes[i], data.Length, offset, images[i].Png);
            offset += data.Length;
        }

        foreach (byte[] data in images.Select(i => i.Data))
        {
            output.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    public static void WriteToFile(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllBytes(path, Build());
    }

    private static void Write(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)(value >> 8));
    }

    private static void WriteEntry(Stream stream, int size, int length, int offset, bool png)
    {
        byte edge = (byte)(size >= 256 ? 0 : size);
        stream.WriteByte(edge);
        stream.WriteByte(edge);
        stream.WriteByte(0);
        stream.WriteByte(0);

        if (png)
        {
            Write(stream, (ushort)0);
            Write(stream, (ushort)0);
        }
        else
        {
            Write(stream, (ushort)1);
            Write(stream, (ushort)32);
        }

        Write(stream, (uint)length);
        Write(stream, (uint)offset);
    }

    private static void Write(Stream stream, ushort reserved, ushort type, ushort count)
    {
        Write(stream, reserved);
        Write(stream, type);
        Write(stream, count);
    }

    private static byte[] EncodeBmp(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int maskStride = ((width + 31) / 32) * 4;
        int pixelBytes = width * height * 4;

        using MemoryStream stream = new();
        Write(stream, 40);
        Write(stream, (uint)width);
        Write(stream, (uint)(height * 2));
        Write(stream, (ushort)1);
        Write(stream, (ushort)32);
        Write(stream, (uint)0);
        Write(stream, (uint)(pixelBytes + (maskStride * height)));
        Write(stream, (uint)0);
        Write(stream, (uint)0);
        Write(stream, (uint)0);
        Write(stream, (uint)0);

        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        byte[] row = new byte[width * 4];
        for (int y = height - 1; y >= 0; y--)
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                byte b = row[(x * 4) + 0];
                byte g = row[(x * 4) + 1];
                byte r = row[(x * 4) + 2];
                byte a = row[(x * 4) + 3];
                row[(x * 4) + 0] = b;
                row[(x * 4) + 1] = g;
                row[(x * 4) + 2] = r;
                row[(x * 4) + 3] = a;
            }

            stream.Write(row, 0, row.Length);
        }

        bitmap.UnlockBits(data);
        stream.Write(new byte[maskStride * height], 0, maskStride * height);
        return stream.ToArray();
    }

    private static void Write(Stream stream, int value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 24) & 0xFF));
    }

    private static void Write(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 24) & 0xFF));
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using MemoryStream stream = new();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    public static void WritePreview(string path, int size)
    {
        using Bitmap bitmap = Render(size);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
