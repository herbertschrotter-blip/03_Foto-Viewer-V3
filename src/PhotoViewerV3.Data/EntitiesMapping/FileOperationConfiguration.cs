using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

internal sealed class FileOperationConfiguration : IEntityTypeConfiguration<FileOperation>
{
    public void Configure(EntityTypeBuilder<FileOperation> b)
    {
        b.ToTable("FileOperations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.BatchId).IsRequired();
        b.Property(x => x.Action).HasConversion<int>();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.SourcePath).IsRequired();
        b.Property(x => x.ExecutedAtUtc).HasConversion(ValueConverters.NullableDateTimeToUnixSeconds);
        b.Property(x => x.UndoneAtUtc).HasConversion(ValueConverters.NullableDateTimeToUnixSeconds);

        b.HasIndex(x => new { x.BatchId, x.Status })
            .HasDatabaseName("IX_FileOperations_Batch_Status");
    }
}
