using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Catalog;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Catalog;

public sealed class CatalogUpdateServiceTests
{
    private static async Task<(Library Library, int AlphaId, int BravoId)> ArrangeScannedLibraryAsync(TempLibraryFixture fx)
    {
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("a", "alpha.jpg"));
        fx.WriteLibraryFile(Path.Combine("a", "bravo.jpg"));
        await fx.ScanAsync(library.Id);
        var alpha = (await fx.FindFileAsync("alpha.jpg"))!;
        var bravo = (await fx.FindFileAsync("bravo.jpg"))!;
        return (library, alpha.Id, bravo.Id);
    }

    [Fact]
    public async Task SetFavorite_setzt_und_entfernt_Favorit_und_sendet_Metadata_Nachricht()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (library, alphaId, bravoId) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateCatalogUpdateService();

        await svc.SetFavoriteAsync(new[] { alphaId, bravoId }, true);

        (await fx.FindFileByIdAsync(alphaId))!.IsFavorite.Should().BeTrue();
        (await fx.FindFileByIdAsync(bravoId))!.IsFavorite.Should().BeTrue();
        var message = fx.Messages.FilesChanged.Should().ContainSingle().Subject;
        message.LibraryId.Should().Be(library.Id);
        message.Kind.Should().Be(FilesChangeKind.Metadata);
        message.FileIds.Should().BeEquivalentTo(new[] { alphaId, bravoId });

        await svc.SetFavoriteAsync(new[] { alphaId }, false);

        (await fx.FindFileByIdAsync(alphaId))!.IsFavorite.Should().BeFalse();
        (await fx.FindFileByIdAsync(bravoId))!.IsFavorite.Should().BeTrue();
        fx.Messages.FilesChanged.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetRating_setzt_und_entfernt_Bewertung()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, bravoId) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateCatalogUpdateService();

        await svc.SetRatingAsync(new[] { alphaId }, 4);

        (await fx.FindFileByIdAsync(alphaId))!.Rating.Should().Be(4);
        (await fx.FindFileByIdAsync(bravoId))!.Rating.Should().BeNull();

        await svc.SetRatingAsync(new[] { alphaId }, null);

        (await fx.FindFileByIdAsync(alphaId))!.Rating.Should().BeNull();
        fx.Messages.OfKind(FilesChangeKind.Metadata).Should().HaveCount(2);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)6)]
    [InlineData((byte)255)]
    public async Task SetRating_wirft_bei_ungueltiger_Bewertung_und_aendert_nichts(byte rating)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, _) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateCatalogUpdateService();

        var act = () => svc.SetRatingAsync(new[] { alphaId }, rating);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await fx.FindFileByIdAsync(alphaId))!.Rating.Should().BeNull();
        fx.Messages.FilesChanged.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_verarbeitet_mehr_als_500_Ids_in_Chunks_und_ignoriert_unbekannte_Ids()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        var now = DateTime.UtcNow;
        var ids = new List<int>();
        await using (var ctx = fx.CreateContext())
        {
            var folder = new Folder { LibraryId = library.Id, Path = fx.LibraryRoot, Name = "library", ScannedAt = now };
            ctx.Folders.Add(folder);
            await ctx.SaveChangesAsync();

            var rows = Enumerable.Range(0, 1200).Select(i => new MediaFile
            {
                LibraryId = library.Id,
                FolderId = folder.Id,
                Name = $"f{i:D4}.jpg",
                Extension = ".jpg",
                Size = i,
                CreatedAtUtc = now,
                ModifiedAtUtc = now,
                MediaType = MediaType.Image,
            }).ToList();
            ctx.Files.AddRange(rows);
            await ctx.SaveChangesAsync();
            ids.AddRange(rows.Select(r => r.Id));
        }

        using var svc = fx.CreateCatalogUpdateService();
        var requested = ids.Concat(new[] { 999_999, 999_998 }).ToList();

        await svc.SetFavoriteAsync(requested, true);

        await using (var ctx = fx.CreateContext())
        {
            (await ctx.Files.CountAsync(f => f.IsFavorite)).Should().Be(1200);
        }

        var message = fx.Messages.FilesChanged.Should().ContainSingle().Subject;
        message.FileIds.Should().HaveCount(1200).And.NotContain(999_999);
    }

    [Fact]
    public async Task Empfaenger_Ausnahme_bricht_das_Update_nicht_ab()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, _) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateCatalogUpdateService();
        var recipient = new object();
        IMessenger messenger = fx.Messenger;
        messenger.Register<object, FilesChangedMessage>(recipient, static (_, _) => throw new InvalidOperationException("Empfänger kaputt"));

        var act = () => svc.SetFavoriteAsync(new[] { alphaId }, true);

        await act.Should().NotThrowAsync();
        (await fx.FindFileByIdAsync(alphaId))!.IsFavorite.Should().BeTrue();
        fx.Messages.OfKind(FilesChangeKind.Metadata).Should().ContainSingle();
    }

    [Fact]
    public async Task Vorab_abgebrochenes_Token_aendert_nichts_und_sendet_keine_Nachricht()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, _) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateCatalogUpdateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => svc.SetFavoriteAsync(new[] { alphaId }, true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await fx.FindFileByIdAsync(alphaId))!.IsFavorite.Should().BeFalse();
        fx.Messages.FilesChanged.Should().BeEmpty();
    }

    [Fact]
    public async Task Leere_Id_Liste_ist_ein_NoOp_ohne_Nachricht()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateCatalogUpdateService();

        await svc.SetFavoriteAsync(Array.Empty<int>(), true);
        await svc.SetRatingAsync(Array.Empty<int>(), 3);

        fx.Messages.FilesChanged.Should().BeEmpty();
    }
}
