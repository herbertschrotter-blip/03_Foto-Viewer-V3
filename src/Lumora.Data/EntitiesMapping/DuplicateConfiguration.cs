using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

// Doppelte erkennen (ADR-0053, Migration AddDuplicateDetection). Fremdschlüssel ohne Navigationen (ADR-0004).

internal sealed class FileFingerprintConfiguration : IEntityTypeConfiguration<FileFingerprint>
{
    public void Configure(EntityTypeBuilder<FileFingerprint> b)
    {
        b.ToTable("FileFingerprints");
        b.HasKey(x => x.FileId);
        b.Property(x => x.FileId).ValueGeneratedNever();
        b.Property(x => x.ImageLowDetail).IsRequired().HasDefaultValue(false);

        b.HasOne<MediaFile>()
            .WithOne()
            .HasForeignKey<FileFingerprint>(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Gruppen „exakt gleich“ und Filter „nur Doppelte“: gleiche Prüfsumme über den Index statt über alle Zeilen.
        b.HasIndex(x => x.ContentHash)
            .HasFilter("ContentHash IS NOT NULL")
            .HasDatabaseName("IX_FileFingerprints_ContentHash");
    }
}

internal sealed class DuplicateCleanupUndoConfiguration : IEntityTypeConfiguration<DuplicateCleanupUndo>
{
    public void Configure(EntityTypeBuilder<DuplicateCleanupUndo> b)
    {
        b.ToTable("DuplicateCleanupUndo");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Snapshot).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.UndoneAtUtc).HasConversion(ValueConverters.NullableDateTimeToUnixSeconds);

        b.HasOne<OperationBatch>()
            .WithMany()
            .HasForeignKey(x => x.BatchId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.BatchId).HasDatabaseName("IX_DuplicateCleanupUndo_Batch");
    }
}
