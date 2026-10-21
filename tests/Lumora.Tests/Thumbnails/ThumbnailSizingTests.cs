using Lumora.Services.Thumbnails;

namespace Lumora.Tests.Thumbnails;

/// <summary>ADR-0043: kurze Kante auf das Ziel, lange höchstens das Dreifache, nie hochskalieren; FFmpeg-Eingabe lesen.</summary>
public sealed class ThumbnailSizingTests
{
    [Theory]
    [InlineData(4000, 3000, 533, 400)] // Querformat 4:3
    [InlineData(3000, 4000, 400, 533)] // Hochformat
    [InlineData(4000, 1800, 889, 400)] // 20:9-Handyfoto
    [InlineData(1000, 1000, 400, 400)]
    [InlineData(12000, 2000, 1200, 200)] // Panorama: lange Kante begrenzt, kurze darunter
    [InlineData(300, 200, 300, 200)] // kleiner als das Ziel: unverändert
    [InlineData(600, 380, 600, 380)] // kurze Kante unter dem Ziel: nie hochskalieren
    [InlineData(0, 100, 0, 0)]
    public void Fit_kurze_Kante_lange_begrenzt_nie_groesser(int width, int height, int expectedWidth, int expectedHeight)
        => ThumbnailSizing.Fit(width, height, ThumbnailSizing.ShortEdge).Should().Be((expectedWidth, expectedHeight));

    [Fact]
    public void Gewaehlte_Werte_aus_Spike_11()
    {
        ThumbnailSizing.ShortEdge.Should().Be(400);
        ThumbnailSizing.MaxLongEdge(ThumbnailSizing.ShortEdge).Should().Be(1200);
        ThumbnailSizing.JpegQuality.Should().Be(82);
        ThumbnailService.Size.Should().Be(ThumbnailSizing.ShortEdge);
        ThumbnailSizing.LegacySizes.Should().Equal(320);
    }

    [Theory]
    [InlineData(6, 3000, 4000)]
    [InlineData(8, 3000, 4000)]
    [InlineData(5, 3000, 4000)]
    [InlineData(7, 3000, 4000)]
    [InlineData(1, 4000, 3000)]
    [InlineData(3, 4000, 3000)]
    public void Oriented_vertauscht_bei_Vierteldrehung(int orientation, int expectedWidth, int expectedHeight)
        => ThumbnailSizing.Oriented(4000, 3000, orientation).Should().Be((expectedWidth, expectedHeight));

    private const string RotatedPhoneVideo = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'C:\x\clip.mp4':
          Metadata:
            major_brand     : isom
          Duration: 00:00:02.00, start: 0.000000, bitrate: 55 kb/s
          Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 1920x1080 [SAR 1:1 DAR 16:9], 51 kb/s, 30 fps, 30 tbr, 10240 tbn (default)
            Metadata:
              handler_name    : VideoHandler
            Side data:
              Display Matrix: rotation of -90.00 degrees
          Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 128 kb/s (default)
        Stream mapping:
          Stream #0:0 -> #0:0 (h264 (native) -> mjpeg (native))
        Output #0, image2, to 'x.jpg.tmp':
          Stream #0:0(und): Video: mjpeg, yuvj420p(pc, progressive), 400x711 [SAR 1:1 DAR 400:711], q=2-31, 200 kb/s, 30 fps, 30 tbn (default)
        """;

    [Fact]
    public void FfmpegStreamInfo_liest_Eingabe_und_Drehung()
    {
        FfmpegStreamInfo.TryParseDisplaySize(RotatedPhoneVideo).Should().Be((1080, 1920));
        FfmpegStreamInfo.TryParseDisplaySize(RotatedPhoneVideo.Replace("rotation of -90.00", "rotation of 180.00")).Should().Be((1920, 1080));
    }

    [Fact]
    public void FfmpegStreamInfo_ohne_Drehung_altes_rotate_Tag_und_Kachelgitter()
    {
        const string plain = "  Stream #0:0: Video: bmp, bgr24, 8x6, 25 fps, 25 tbr, 25 tbn\r\nStream mapping:\r\n";
        FfmpegStreamInfo.TryParseDisplaySize(plain).Should().Be((8, 6));

        const string rotateTag = "  Stream #0:0(eng): Video: h264 (avc1 / 0x31637661), yuv420p, 1280x720, 30 fps\n    Metadata:\n      rotate          : 270\n";
        FfmpegStreamInfo.TryParseDisplaySize(rotateTag).Should().Be((720, 1280));

        const string heif = "  Stream group #0:0[0x31]: Tile Grid: hevc (Main Still Picture), yuvj420p(pc), 4032x3024 (default)\n" +
                            "  Stream #0:0[0x1]: Video: hevc (Main Still Picture) (hvc1 / 0x31637668), yuvj420p(pc), 512x512\n";
        FfmpegStreamInfo.TryParseDisplaySize(heif).Should().Be((4032, 3024), "das Kachelgitter ist das Bild, die erste Spur nur eine Kachel");
    }

    [Fact]
    public void FfmpegStreamInfo_ueberspringt_eingebettetes_Titelbild()
    {
        const string withCover = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'x.mp4':
              Stream #0:0[0x1]: Video: mjpeg (Baseline), yuvj420p(pc), 600x600 [SAR 1:1 DAR 1:1], 90k tbr (attached pic)
              Stream #0:1[0x2](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p, 1920x1080, 30 fps (default)
                Side data:
                  Display Matrix: rotation of 90.00 degrees
            Stream mapping:
            """;
        FfmpegStreamInfo.TryParseDisplaySize(withCover).Should().Be((1080, 1920));
    }

    [Theory]
    [InlineData("Input #0, mov,mp4 from 'x':", true)]
    [InlineData("Stream mapping:", true)]
    [InlineData("Output #0, image2, to 'x.jpg.tmp':", true)]
    [InlineData("frame=    1 fps=0.0 q=4.0 Lsize=N/A", true)]
    [InlineData("  Stream #0:0: Video: h264", true)]
    [InlineData("[out#0/image2 @ 0000] video:10KiB audio:0KiB muxing overhead: unknown", true)]
    [InlineData("[in#0 @ 0000] Error opening input: Invalid data found when processing input", false)]
    [InlineData("Output file is empty, nothing was encoded", false)]
    public void Ffmpeg_Fehlertext_ohne_Info_Zeilen(string line, bool info)
        => FfmpegVideoThumbnailer.IsInfoLine(line).Should().Be(info);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[in#0 @ 000001] Error opening input: Invalid data found when processing input")]
    [InlineData("Output #0, image2, to 'x':\n  Stream #0:0: Video: mjpeg, yuvj420p, 400x300\n")]
    public void FfmpegStreamInfo_ohne_Eingabe_Beschreibung_null(string? stderr)
        => FfmpegStreamInfo.TryParseDisplaySize(stderr).Should().BeNull();

    [Fact]
    public void Scale_Filter_rechnet_kurze_Kante_und_Grenze()
        => FfmpegVideoThumbnailer.BuildScaleFilter(400).Should().Be(
            "scale=w='max(2,round(iw*min(1,min(400/min(iw,ih),1200/max(iw,ih)))/2)*2)':h=-2");
}
