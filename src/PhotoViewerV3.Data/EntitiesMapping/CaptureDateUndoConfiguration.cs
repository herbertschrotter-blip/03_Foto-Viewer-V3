using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

/// <summary>Alte Werte des Aufnahmedatums je Operation „Datum setzen“ (ADR-0046).</summary>
internal sealed class CaptureDateUndoConfiguration : IEntityTypeConfiguration<CaptureDateUndo>
{
    public void Configure(EntityTypeBuilder<CaptureDateUndo> b)
    {
        b.ToTable("CaptureDateUndo");
        b.HasKey(x => x.FileOperationId);
        b.Property(x => x.FileOperationId).ValueGeneratedNever();
        b.Property(x => x.OldCapturedAt).HasConversion(ValueConverters.NullableDateTimeToUnixSeconds);
        b.Property(x => x.OldSource).HasConversion<int?>();
        b.Property(x => x.OldPrecision).HasConversion<int>();
        b.HasOne<FileOperation>()
            .WithOne()
            .HasForeignKey<CaptureDateUndo>(x => x.FileOperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
