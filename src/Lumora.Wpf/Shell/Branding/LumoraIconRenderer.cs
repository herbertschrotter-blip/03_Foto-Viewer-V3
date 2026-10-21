using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumora.Shell.Branding;

/// <summary>
/// Erzeugt <c>Lumora.ico</c> reproduzierbar aus der Vektor-Quelle (<c>LumoraIcon.xaml</c>): jede Größe per
/// <see cref="RenderTargetBitmap"/> gerendert (unter 32 px die vereinfachte Variante), dann als ICO-Container
/// geschrieben — 16–128 px als 32-Bit-DIB mit Alphakanal (breiteste Verträglichkeit mit Explorer/Taskleiste), 256 px
/// als PNG. Aufruf über <c>build/make-icon.ps1</c> (Test <c>LumoraIconFileTests</c> mit <c>LUMORA_WRITE_ICON=1</c>).
/// </summary>
public static class LumoraIconRenderer
{
    /// <summary>Größen im ICO (px).</summary>
    public static IReadOnlyList<int> IcoSizes { get; } = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    /// <summary>Ab dieser Größe wird der Rahmen als PNG statt als DIB gespeichert.</summary>
    public const int PngFromSize = 256;

    /// <summary>Symbol in der Pixelgröße rendern (96 dpi, Pbgra32, eingefroren).</summary>
    public static BitmapSource Render(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 128.0, size / 128.0));
            dc.DrawDrawing(LumoraIconImages.DrawingFor(size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Alle <see cref="IcoSizes"/> rendern und als ICO schreiben.</summary>
    public static void WriteIco(Stream target) => WriteIco(target, IcoSizes.Select(Render));

    /// <summary>Quadratische Rahmen (1–256 px) als ICO-Container schreiben.</summary>
    public static void WriteIco(Stream target, IEnumerable<BitmapSource> frames)
    {
        var images = frames.Select(f => (Size: f.PixelWidth, Data: f.PixelWidth >= PngFromSize ? EncodePng(f) : EncodeDib(f))).ToList();
        if (images.Count == 0 || images.Any(i => i.Size is < 1 or > 256))
        {
            throw new ArgumentException("ICO braucht 1–256 px große Rahmen.", nameof(frames));
        }

        using var writer = new BinaryWriter(target, System.Text.Encoding.ASCII, leaveOpen: true);
        // ICONDIR
        writer.Write((ushort)0); // reserviert
        writer.Write((ushort)1); // Typ 1 = Symbol
        writer.Write((ushort)images.Count);
        // ICONDIRENTRY je Rahmen
        var offset = 6 + (16 * images.Count);
        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 = 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); // keine Palette
            writer.Write((byte)0); // reserviert
            writer.Write((ushort)1); // Farbebenen
            writer.Write((ushort)32); // Bit je Pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in images)
        {
            writer.Write(data);
        }
    }

    /// <summary>Größen (px) der Rahmen eines ICO-Containers aus dem Verzeichnis (ohne die Bilder zu dekodieren).</summary>
    public static IReadOnlyList<int> ReadIcoSizes(Stream source)
    {
        using var reader = new BinaryReader(source, System.Text.Encoding.ASCII, leaveOpen: true);
        if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
        {
            throw new InvalidDataException("Keine ICO-Datei.");
        }

        var count = reader.ReadUInt16();
        var sizes = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            var width = reader.ReadByte();
            reader.ReadBytes(15);
            sizes.Add(width == 0 ? 256 : width);
        }

        return sizes;
    }

    private static byte[] EncodePng(BitmapSource frame)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>BITMAPINFOHEADER + 32-Bit-BGRA (nicht vormultipliziert, Zeilen von unten) + AND-Maske (1 = durchsichtig).</summary>
    private static byte[] EncodeDib(BitmapSource frame)
    {
        var size = frame.PixelWidth;
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = size * 4;
        var pixels = new byte[stride * size];
        bgra.CopyPixels(pixels, stride, 0);

        var maskStride = ((size + 31) / 32) * 4;
        var mask = new byte[maskStride * size];
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(40); // biSize
            writer.Write(size); // biWidth
            writer.Write(size * 2); // biHeight: Farbe + Maske
            writer.Write((ushort)1); // biPlanes
            writer.Write((ushort)32); // biBitCount
            writer.Write(0); // BI_RGB
            writer.Write(pixels.Length + mask.Length); // biSizeImage
            writer.Write(0); // biXPelsPerMeter
            writer.Write(0); // biYPelsPerMeter
            writer.Write(0); // biClrUsed
            writer.Write(0); // biClrImportant

            for (var y = size - 1; y >= 0; y--)
            {
                writer.Write(pixels, y * stride, stride);
                for (var x = 0; x < size; x++)
                {
                    if (pixels[(y * stride) + (x * 4) + 3] == 0)
                    {
                        mask[((size - 1 - y) * maskStride) + (x / 8)] |= (byte)(0x80 >> (x % 8));
                    }
                }
            }

            writer.Write(mask);
        }

        return stream.ToArray();
    }
}
