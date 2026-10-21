using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Services.Faces.Crops;
using Lumora.Thumbnails;

namespace Lumora.Persons;

/// <summary>
/// WIC-Implementierung von <see cref="IFaceCropRenderer"/> (ADR-0037): wie der Thumbnail-Renderer zwei Durchgänge —
/// Kopf (Maße, EXIF-Orientierung) ohne Pixel, dann skaliertes Dekodieren GERADE so groß, dass das kleinste Gesicht der
/// Datei samt Rand ≥ <c>size</c> Pixel hat (JPEG dekodiert verkleinert, schnell). Danach Orientierung, je Gesicht ein
/// quadratischer Ausschnitt (Box + <see cref="FaceCropDefaults.Margin"/>, ins Bild geschoben), auf <c>size</c> skaliert,
/// JPEG über .tmp + Umbenennen. Thread-sicher (keine gemeinsamen Objekte).
/// </summary>
public sealed class WicFaceCropRenderer : IFaceCropRenderer
{
    private const int JpegQuality = 85;

    public void Render(string sourcePath, IReadOnlyList<FaceCropTarget> targets, int size)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            return;
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Bilddatei fehlt.", sourcePath);
        }

        int storedWidth;
        int storedHeight;
        int orientation;
        using (var header = OpenShared(sourcePath))
        {
            var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var first = decoder.Frames[0];
            storedWidth = first.PixelWidth;
            storedHeight = first.PixelHeight;
            orientation = WicImageThumbnailRenderer.ReadOrientation(first.Metadata as BitmapMetadata);
        }

        var swap = orientation is >= 5 and <= 8;
        var orientedWidth = swap ? storedHeight : storedWidth;
        var orientedHeight = swap ? storedWidth : storedHeight;

        // Kleinstes Gesicht bestimmt die nötige Auflösung (Seite inkl. Rand in Pixeln des vollen Bildes)
        var smallestSide = targets.Min(t => SideOf(t.BoxRelative, orientedWidth, orientedHeight));
        var scale = smallestSide <= 0 ? 1.0 : Math.Min(1.0, size / smallestSide);
        var longer = Math.Max(storedWidth, storedHeight);
        var decodeLonger = (int)Math.Ceiling(longer * scale);

        var bitmap = new BitmapImage();
        using (var stream = OpenShared(sourcePath))
        {
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeLonger < longer)
            {
                if (storedWidth >= storedHeight)
                {
                    bitmap.DecodePixelWidth = decodeLonger;
                }
                else
                {
                    bitmap.DecodePixelHeight = decodeLonger;
                }
            }

            bitmap.EndInit();
        }

        bitmap.Freeze();
        BitmapSource oriented = WicImageThumbnailRenderer.ApplyOrientation(bitmap, orientation);
        if (!WicImageThumbnailRenderer.IsOpaque(oriented.Format))
        {
            oriented = WicImageThumbnailRenderer.FlattenAlpha(oriented, WicImageThumbnailRenderer.AlphaBackground);
        }

        foreach (var target in targets)
        {
            var rect = CropRect(target.BoxRelative, oriented.PixelWidth, oriented.PixelHeight);
            if (rect.Width < 1 || rect.Height < 1)
            {
                continue;
            }

            BitmapSource crop = new CroppedBitmap(oriented, rect);
            var factor = (double)size / rect.Width;
            if (Math.Abs(factor - 1.0) > 0.001)
            {
                crop = new TransformedBitmap(crop, new ScaleTransform(factor, factor));
            }

            crop.Freeze();
            Save(crop, target.TargetPath);
        }
    }

    /// <summary>Seitenlänge des quadratischen Ausschnitts (Box + Rand) in Pixeln eines Bildes der Größe w × h.</summary>
    internal static double SideOf(System.Drawing.RectangleF box, int width, int height)
        => Math.Max(box.Width * width, box.Height * height) * (1 + 2 * FaceCropDefaults.Margin);

    /// <summary>Quadrat um die Boxmitte, ins Bild geschoben; höchstens so groß wie die kürzere Bildseite.</summary>
    internal static Int32Rect CropRect(System.Drawing.RectangleF box, int width, int height)
    {
        var side = Math.Min(SideOf(box, width, height), Math.Min(width, height));
        var cx = (box.X + box.Width / 2) * width;
        var cy = (box.Y + box.Height / 2) * height;
        var x = Math.Clamp(cx - side / 2, 0, width - side);
        var y = Math.Clamp(cy - side / 2, 0, height - side);
        var s = (int)Math.Floor(side);
        return new Int32Rect((int)Math.Round(x), (int)Math.Round(y), Math.Min(s, width - (int)Math.Round(x)), Math.Min(s, height - (int)Math.Round(y)));
    }

    private static void Save(BitmapSource image, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = targetPath + ".tmp";
        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(tempPath))
        {
            encoder.Save(stream);
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
}
