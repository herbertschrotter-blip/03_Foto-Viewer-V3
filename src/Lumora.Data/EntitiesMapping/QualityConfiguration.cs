using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

// Foto-Güte (ADR-0057, Migration AddFileQuality). Fremdschlüssel ohne Navigation (ADR-0004).

internal sealed class FileQualityConfiguration : IEntityTypeConfiguration<FileQuality>
{
    public void Configure(EntityTypeBuilder<FileQuality> b)
    {
        b.ToTable("FileQuality");
        b.HasKey(x => x.FileId);
        b.Property(x => x.FileId).ValueGeneratedNever();

        b.HasOne<MediaFile>()
            .WithOne()
            .HasForeignKey<FileQuality>(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Kein weiterer Index: Zählen und Stapel laufen über Files (Bibliothek) mit Primärschlüssel-Lookup hier.
    }
}
