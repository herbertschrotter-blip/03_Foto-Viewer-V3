using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

/// <summary>Rückgängig-Speicher je Operation „Ort setzen/entfernen“ (ADR-0050).</summary>
internal sealed class GeoTagUndoConfiguration : IEntityTypeConfiguration<GeoTagUndo>
{
    public void Configure(EntityTypeBuilder<GeoTagUndo> b)
    {
        b.ToTable("GeoTagUndo");
        b.HasKey(x => x.FileOperationId);
        b.Property(x => x.FileOperationId).ValueGeneratedNever();
        b.Property(x => x.OldGeoSource).HasConversion<int?>();
        b.Property(x => x.WriteKind).HasConversion<int>();
        b.HasOne<FileOperation>()
            .WithOne()
            .HasForeignKey<GeoTagUndo>(x => x.FileOperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
