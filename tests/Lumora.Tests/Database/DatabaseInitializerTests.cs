using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Database;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public async Task Initialize_setzt_Pending_Thumbnails_auf_Stale_und_zaehlt_unfertige_Batches_ohne_sie_zu_veraendern()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var now = DateTime.UtcNow;
        await using (var ctx = fx.CreateContext())
        {
            ctx.Thumbnails.AddRange(
                Row(1, ThumbnailStatus.Pending),
                Row(2, ThumbnailStatus.Generated),
                Row(3, ThumbnailStatus.Failed));
            ctx.OperationBatches.AddRange(
                new OperationBatch { OperationType = OperationType.Move, Status = BatchStatus.Applying, CreatedAtUtc = now },
                new OperationBatch { OperationType = OperationType.Rename, Status = BatchStatus.Undoing, CreatedAtUtc = now },
                new OperationBatch { OperationType = OperationType.Recycle, Status = BatchStatus.Applied, CreatedAtUtc = now, CompletedAtUtc = now });
            await ctx.SaveChangesAsync();
        }

        var result = await new DatabaseInitializer(fx.ContextFactory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        result.NewlyMigrated.Should().Be(0);
        result.AppliedMigrations.Should().BeGreaterThan(0);
        result.StaleThumbnailsRecovered.Should().Be(1);
        result.UnfinishedBatches.Should().Be(2);

        await using var verify = fx.CreateContext();
        (await verify.Thumbnails.SingleAsync(t => t.FileId == 1)).Status.Should().Be(ThumbnailStatus.Stale);
        (await verify.Thumbnails.SingleAsync(t => t.FileId == 2)).Status.Should().Be(ThumbnailStatus.Generated);
        (await verify.Thumbnails.SingleAsync(t => t.FileId == 3)).Status.Should().Be(ThumbnailStatus.Failed);
        (await verify.OperationBatches.CountAsync(b => b.Status == BatchStatus.Applying)).Should().Be(1, "unfertige Batches werden nur gemeldet");
        (await verify.OperationBatches.CountAsync(b => b.Status == BatchStatus.Undoing)).Should().Be(1);
    }

    private static Thumbnail Row(int fileId, ThumbnailStatus status) => new()
    {
        FileId = fileId,
        Size = 320,
        Kind = ThumbnailKind.Still,
        Format = "jpg",
        CachePath = $"{fileId}_320_still.jpg",
        SourceModifiedAtUtc = DateTime.UtcNow,
        Status = status,
    };
}
