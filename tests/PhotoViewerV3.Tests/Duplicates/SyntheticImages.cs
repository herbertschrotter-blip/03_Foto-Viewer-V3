using PhotoViewerV3.Services.Faces;

namespace PhotoViewerV3.Tests.Duplicates;

/// <summary>
/// Synthetische „Fotos“ als RGB24 für die Fingerabdruck-Tests (keine Fremd-Assets): Farbverlauf, Ellipsen und Rechtecke in
/// zufälligen Farben, leichtes Rauschen. Dazu die Abwandlungen, die „ähnlich“ finden soll (verkleinert, neu kodiert,
/// beschnitten, Helligkeit), jeweils ohne WIC.
/// </summary>
internal static class SyntheticImages
{
    public sealed record Rgb(byte[] Pixels, int Width, int Height)
    {
        public FaceImage ToFaceImage() => new(Pixels, Width, Height);
    }

    /// <summary>Szene aus Formen; gleiche <paramref name="seed"/> = gleiches Motiv.</summary>
    public static Rgb Scene(int seed, int width = 640, int height = 480)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 3];
        var (r0, g0, b0) = (random.Next(256), random.Next(256), random.Next(256));
        var (r1, g1, b1) = (random.Next(256), random.Next(256), random.Next(256));
        var shapes = Enumerable.Range(0, 7).Select(_ => (
            Cx: random.NextDouble(), Cy: random.NextDouble(),
            Rx: 0.06 + (random.NextDouble() * 0.25), Ry: 0.06 + (random.NextDouble() * 0.25),
            Ellipse: random.Next(2) == 0,
            R: random.Next(256), G: random.Next(256), B: random.Next(256))).ToArray();
        var noise = new Random(seed * 7919);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var t = (x + y) / (double)(width + height);
                double r = r0 + ((r1 - r0) * t), g = g0 + ((g1 - g0) * t), b = b0 + ((b1 - b0) * t);
                var u = x / (double)width;
                var v = y / (double)height;
                foreach (var s in shapes)
                {
                    var dx = (u - s.Cx) / s.Rx;
                    var dy = (v - s.Cy) / s.Ry;
                    var inside = s.Ellipse ? (dx * dx) + (dy * dy) <= 1 : Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1;
                    if (inside)
                    {
                        (r, g, b) = (s.R, s.G, s.B);
                    }
                }

                var n = noise.Next(-3, 4);
                var p = ((y * width) + x) * 3;
                pixels[p] = Clamp(r + n);
                pixels[p + 1] = Clamp(g + n);
                pixels[p + 2] = Clamp(b + n);
            }
        }

        return new Rgb(pixels, width, height);
    }

    /// <summary>Flächengetreu verkleinert (wie ein Vorschaubild oder eine WhatsApp-Kopie).</summary>
    public static Rgb Downscale(Rgb image, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var y0 = y * image.Height / height;
            var y1 = Math.Max(y0 + 1, (y + 1) * image.Height / height);
            for (var x = 0; x < width; x++)
            {
                var x0 = x * image.Width / width;
                var x1 = Math.Max(x0 + 1, (x + 1) * image.Width / width);
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var sy = y0; sy < y1; sy++)
                    {
                        for (var sx = x0; sx < x1; sx++)
                        {
                            sum += image.Pixels[(((sy * image.Width) + sx) * 3) + c];
                        }
                    }

                    pixels[(((y * width) + x) * 3) + c] = (byte)(sum / ((y1 - y0) * (x1 - x0)));
                }
            }
        }

        return new Rgb(pixels, width, height);
    }

    /// <summary>„Neu gespeichert“: Farbstufen vergröbert, 8 × 8-Blöcke leicht geglättet, neues Rauschen (wie verlustbehaftete Kompression).</summary>
    public static Rgb Recode(Rgb image, int seed = 1)
    {
        var random = new Random(seed);
        var pixels = (byte[])image.Pixels.Clone();
        for (var by = 0; by < image.Height; by += 8)
        {
            for (var bx = 0; bx < image.Width; bx += 8)
            {
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, count = 0;
                    for (var y = by; y < Math.Min(by + 8, image.Height); y++)
                    {
                        for (var x = bx; x < Math.Min(bx + 8, image.Width); x++)
                        {
                            sum += pixels[(((y * image.Width) + x) * 3) + c];
                            count++;
                        }
                    }

                    var mean = sum / count;
                    for (var y = by; y < Math.Min(by + 8, image.Height); y++)
                    {
                        for (var x = bx; x < Math.Min(bx + 8, image.Width); x++)
                        {
                            var p = (((y * image.Width) + x) * 3) + c;
                            var blended = ((pixels[p] * 3) + mean) / 4;
                            pixels[p] = Clamp((Math.Round(blended / 10.0) * 10) + random.Next(-4, 5));
                        }
                    }
                }
            }
        }

        return image with { Pixels = pixels };
    }

    /// <summary>Rand abgeschnitten (Anteil je Seite).</summary>
    public static Rgb Crop(Rgb image, double fraction)
    {
        var left = (int)(image.Width * fraction);
        var top = (int)(image.Height * fraction);
        var width = image.Width - (2 * left);
        var height = image.Height - (2 * top);
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(image.Pixels, (((y + top) * image.Width) + left) * 3, pixels, y * width * 3, width * 3);
        }

        return new Rgb(pixels, width, height);
    }

    /// <summary>Heller/dunkler.</summary>
    public static Rgb Brighten(Rgb image, int delta)
        => image with { Pixels = image.Pixels.Select(p => Clamp(p + delta)).ToArray() };

    /// <summary>Einfarbig (z. B. schwarzes Foto).</summary>
    public static Rgb Flat(byte value, int width = 320, int height = 240)
        => new(Enumerable.Repeat(value, width * height * 3).ToArray(), width, height);

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}

/// <summary>Bild-Lader-Fake: liefert zu einem Dateipfad (Name) ein vorgegebenes Bild, sonst eine Szene aus dem Namen.</summary>
internal sealed class FakeImageLoader : IFaceImageLoader
{
    private readonly Dictionary<string, SyntheticImages.Rgb> _images = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Loaded { get; } = new();

    public FakeImageLoader Add(string fileName, SyntheticImages.Rgb image)
    {
        _images[fileName] = image;
        return this;
    }

    public FaceImage Load(string path, int maxEdge)
    {
        lock (Loaded)
        {
            Loaded.Add(path);
        }

        var name = Path.GetFileName(path);
        if (name.Contains("kaputt", StringComparison.OrdinalIgnoreCase))
        {
            throw new FaceImageDecodeException("kaputt");
        }

        if (_images.TryGetValue(name, out var image))
        {
            return image.ToFaceImage();
        }

        return SyntheticImages.Scene(StringComparer.OrdinalIgnoreCase.GetHashCode(name), 160, 120).ToFaceImage();
    }
}
