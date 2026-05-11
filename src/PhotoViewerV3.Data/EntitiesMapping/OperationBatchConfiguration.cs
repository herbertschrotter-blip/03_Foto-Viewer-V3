using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

internal sealed class OperationBatchConfiguration : IEntityTypeConfiguration<OperationBatch>
{
    public void Configure(EntityTypeBuilder<OperationBatch> b)
    {
        b.ToTable("OperationBatches");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.OperationType).HasConversion<int>();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.CompletedAtUtc).HasConversion(ValueConverters.NullableDateTimeToUnixSeconds);
    }
}
