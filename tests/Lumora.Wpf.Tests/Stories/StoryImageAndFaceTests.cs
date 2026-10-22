using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Domain.Enums;
using Lumora.Services.Thumbnails;
using Lumora.Stories;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Stories;

/// <summary>
/// Bild-Lieferung der Story-Seite (ADR-0060): Anforderungen werden entdoppelt, „groß“ ersetzt ein kleines Bild, eine
/// unbeantwortete Anforderung darf später erneut; Gesichts-Stapel mit doppelten Ids werfen nicht.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class StoryImageAndFaceTests
{
    private static StoryMediaInfo Media(int id) => new(id, 1, $@"C:\fake\{id}.jpg", $"{id}.jpg", MediaType.Image, 1, DateTime.UtcNow, 3000, 2000, false, null, null, null, null);

    [Fact]
    public void Anforderungen_werden_entdoppelt_gross_wird_nachgefordert()
    {
        var thumbnails = new CountingThumbnails();
        using var provider = new StoryImageProvider(thumbnails);

        provider.Request([Media(1), Media(2)]);
        provider.Request([Media(1)]);
        provider.Request([Media(1)], large: true);

        thumbnails.Requested.Should().Equal(new[] { 1, 2 }, "eine laufende Anforderung wird nicht wiederholt; groß wird beim Eintreffen beachtet");
    }

    [Fact]
    public Task Gesichter_mit_doppelten_Ids_im_Stapel_werfen_nicht() => TestUiThread.Dispatcher.InvokeAsync(async () =>
    {
        var kit = new StoryTestKit();
        kit.Stories.Add(FakeStoryService.Card(4, StoryKind.Person, "Anna · 2019", personId: 1));
        var faces = new FakeFaces();
        var services = new StoryPageServices(kit.Stories, kit.Media, kit.Images, kit.Dialogs, kit.Navigation, faces, kit.Slideshow, kit.Lightbox);
        var vm = new StoriesViewModel(services, Microsoft.Extensions.Logging.Abstractions.NullLogger<StoriesViewModel>.Instance, kit.Badge, kit.Messenger);
        await vm.LoadAsync();
        var row = vm.Rows.OfType<OverviewCardRow>().First();
        await vm.RequestRowAsync(row);
        faces.Requested.Should().Contain(11);

        var image = Pixel();
        var raise = () => faces.Raise([(11L, image), (11L, image)]);

        raise.Should().NotThrow();
        row.Cards[0].Avatars.First(a => a.Info.FaceId == 11).Image.Should().BeSameAs(image);
        row.Cards[0].PersonFace.Should().BeSameAs(image, "Personen-Story zeigt das Gesicht der Person");
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));

    private static BitmapSource Pixel()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class FakeFaces : IStoryFaceSource
    {
        public List<long> Requested { get; } = [];

        public event Action<IReadOnlyList<(long FaceId, ImageSource Image)>>? ImagesReady;

        public ImageSource? TryGet(long faceId) => null;

        public void Request(int libraryId, IReadOnlyCollection<long> faceIds) => Requested.AddRange(faceIds);

        public void Raise(IReadOnlyList<(long, ImageSource)> batch) => ImagesReady?.Invoke(batch);
    }

    private sealed class CountingThumbnails : IThumbnailService
    {
        public List<int> Requested { get; } = [];

        public int ThumbnailSize => 400;

        public int PendingCount => 0;

        public event Action<ThumbnailReadyEventArgs>? ThumbnailReady
        {
            add { }
            remove { }
        }

        public Task RequestThumbnailsAsync(IReadOnlyList<ThumbnailRequest> requests, CancellationToken cancellationToken = default)
        {
            Requested.AddRange(requests.Select(r => r.FileId));
            return Task.CompletedTask;
        }
    }
}
