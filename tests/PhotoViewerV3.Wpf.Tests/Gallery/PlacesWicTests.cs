using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoViewerV3.Gallery.Places;
using PhotoViewerV3.Services.Metadata;
using PhotoViewerV3.Services.Places;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// GPS gegen WIC geprüft (ADR-0050): Lumoras Leser liest, was der WIC-JPEG-Encoder als GPS schreibt, und WIC liest, was
/// Lumoras verlustfreier Schreiber ins EXIF setzt (Spezifikationstreue beider Richtungen). Dazu das echte Dialogfenster.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class PlacesWicTests
{
    [Fact]
    public void WIC_GPS_wird_gelesen()
    {
        var bytes = TestUiThread.Run(() => EncodeWithGps(-33.8688, 151.2093), TimeSpan.FromSeconds(20));

        var reading = MediaGpsReader.Read(new MemoryStream(bytes));

        reading.Should().NotBeNull();
        reading!.Value.Coordinate.Latitude.Should().BeApproximately(-33.8688, 1e-6);
        reading.Value.Coordinate.Longitude.Should().BeApproximately(151.2093, 1e-6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lumoras_GPS_liest_WIC(bool startWithWicExif)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pv3-wicgps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "a.jpg");
            File.WriteAllBytes(path, TestUiThread.Run(() => Encode(startWithWicExif), TimeSpan.FromSeconds(20)));
            GeoFileWriter.Write(path, 0, new GeoCoordinate(47.0707, -15.4395), keepModifiedTime: true);

            var (lat, latRef, lon, lonRef, date) = TestUiThread.Run(() =>
            {
                using var stream = File.OpenRead(path);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var metadata = (BitmapMetadata)frame.Metadata;
                frame.PixelWidth.Should().Be(32, "Bild bleibt dekodierbar");
                return ((ulong[])metadata.GetQuery("/app1/ifd/gps/{ushort=2}"), (string)metadata.GetQuery("/app1/ifd/gps/{ushort=1}"),
                        (ulong[])metadata.GetQuery("/app1/ifd/gps/{ushort=4}"), (string)metadata.GetQuery("/app1/ifd/gps/{ushort=3}"),
                        metadata.GetQuery("/app1/ifd/exif/{ushort=36867}") as string);
            }, TimeSpan.FromSeconds(20));

            Degrees(lat).Should().BeApproximately(47.0707, 1e-6);
            latRef.Should().Be("N");
            Degrees(lon).Should().BeApproximately(15.4395, 1e-6);
            lonRef.Should().Be("W");
            if (startWithWicExif)
            {
                date.Should().Be("2024:07:12 15:30:12", "übriges EXIF bleibt erhalten");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Dialog_Enter_uebernimmt_den_ersten_Suchtreffer_dann_den_Ort()
    {
        var result = TestUiThread.Run(() =>
        {
            var vm = new SetPlaceViewModel(new PlaceDialogRequest("Auswahl", 1, null, []), new ReverseGeocoder());
            var window = new SetPlaceDialog(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                vm.SearchText = "Graz";
                vm.SelectedHit = vm.SearchResults[0];
                window.Accept();
                if (window.IsVisible)
                {
                    window.Close();
                }
            });
            _ = window.ShowDialog();
            return window.Result;
        }, TimeSpan.FromSeconds(20));

        result.Should().NotBeNull();
        result!.Remove.Should().BeFalse();
        result.Latitude!.Value.Should().BeApproximately(47.07, 0.05);
        result.KeepModifiedTime.Should().BeTrue();
    }

    private static double Degrees(ulong[] rationals)
    {
        static double R(ulong v) => (uint)(v & 0xFFFFFFFF) / (double)(uint)(v >> 32);
        return R(rationals[0]) + (R(rationals[1]) / 60) + (R(rationals[2]) / 3600);
    }

    private static byte[] Encode(bool withExif)
    {
        var pixels = new byte[32 * 24 * 4];
        Array.Fill(pixels, (byte)90);
        var source = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        var encoder = new JpegBitmapEncoder();
        BitmapMetadata? metadata = null;
        if (withExif)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", "2024:07:12 15:30:12");
        }

        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] EncodeWithGps(double latitude, double longitude)
    {
        var pixels = new byte[32 * 24 * 4];
        var source = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/gps/{ushort=1}", latitude < 0 ? "S" : "N");
        metadata.SetQuery("/app1/ifd/gps/{ushort=2}", Dms(Math.Abs(latitude)));
        metadata.SetQuery("/app1/ifd/gps/{ushort=3}", longitude < 0 ? "W" : "E");
        metadata.SetQuery("/app1/ifd/gps/{ushort=4}", Dms(Math.Abs(longitude)));
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();

        static ulong[] Dms(double value)
        {
            var d = Math.Floor(value);
            var m = Math.Floor((value - d) * 60);
            var s = Math.Round((((value - d) * 60) - m) * 60 * 1000);
            return [Pack((uint)d, 1), Pack((uint)m, 1), Pack((uint)s, 1000)];
        }

        static ulong Pack(uint numerator, uint denominator) => ((ulong)denominator << 32) | numerator;
    }
}
