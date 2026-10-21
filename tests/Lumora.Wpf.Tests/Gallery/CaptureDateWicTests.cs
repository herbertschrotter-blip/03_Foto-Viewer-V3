using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumora.Domain.Enums;
using Lumora.Gallery.CaptureDates;
using Lumora.Services.Metadata;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Aufnahmedatum mit echten WIC-Dateien (ADR-0046): Der verwaltete Leser liest, was der WIC-JPEG/TIFF-Encoder als EXIF
/// schreibt (gleiche Quelle wie der Rauchtest), und das echte Dialogfenster übernimmt mit Enter.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CaptureDateWicTests
{
    [Fact]
    public void WIC_JPEG_mit_DateTimeOriginal_und_Zone_wird_gelesen()
    {
        var bytes = TestUiThread.Run(() => Encode(new JpegBitmapEncoder(), "jpg", "2024:07:12 15:30:12", "+02:00"), TimeSpan.FromSeconds(20));

        var value = MediaDateReader.Read(new MemoryStream(bytes));

        value.Should().NotBeNull();
        value!.Value.Source.Should().Be(CaptureDateSource.Exif);
        value.Value.Utc.Should().Be(new DateTime(2024, 7, 12, 13, 30, 12, DateTimeKind.Utc));
    }

    [Fact]
    public void WIC_TIFF_mit_DateTimeOriginal_wird_gelesen()
    {
        var bytes = TestUiThread.Run(() => Encode(new TiffBitmapEncoder(), "tiff", "1975:05:17 10:20:30", null), TimeSpan.FromSeconds(20));

        MediaDateReader.Read(new MemoryStream(bytes))!.Value.Local.Should().Be(new DateTime(1975, 5, 17, 10, 20, 30));
    }

    [Fact]
    public void Dialog_Enter_uebernimmt_die_gueltige_Eingabe()
    {
        var result = TestUiThread.Run(() =>
        {
            var vm = new SetCaptureDateViewModel(new CaptureDateDialogRequest("Auswahl", 2)) { Mode = CaptureDateDialogMode.Approximate, ApproximateYear = "1982" };
            var window = new SetCaptureDateDialog(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                window.Accept();
                if (window.IsVisible)
                {
                    window.Close();
                }
            });
            _ = window.ShowDialog();
            return window.Result;
        }, TimeSpan.FromSeconds(20));

        result.Should().Be(new CaptureDateDialogResult(new CaptureDateEdit.Approximate(1982, null), false));
    }

    private static byte[] Encode(BitmapEncoder encoder, string format, string dateTimeOriginal, string? offset)
    {
        var pixels = new byte[32 * 24 * 4];
        Array.Fill(pixels, (byte)120);
        var source = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        var metadata = new BitmapMetadata(format);
        var prefix = format == "jpg" ? "/app1/ifd/exif" : "/ifd/exif";
        metadata.SetQuery(prefix + "/{ushort=36867}", dateTimeOriginal);
        if (offset is not null)
        {
            metadata.SetQuery(prefix + "/{ushort=36881}", offset);
        }

        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
