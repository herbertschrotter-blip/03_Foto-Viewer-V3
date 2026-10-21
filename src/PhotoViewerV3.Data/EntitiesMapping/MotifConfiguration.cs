using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

// Motiv-Erkennung (ADR-0052, Migration AddMotifs). Fremdschlüssel ohne Navigationen (ADR-0004), von e_sqlite3 erzwungen.

internal sealed class MotifTermConfiguration : IEntityTypeConfiguration<MotifTerm>
{
    public void Configure(EntityTypeBuilder<MotifTerm> b)
    {
        b.ToTable("MotifTerms");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).IsRequired().UseCollation("NOCASE");
        b.Property(x => x.SortOrder).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // Bereichsbezug (Bibliotheks-Trennung): vorerst die Bibliothek; Bibliothek gelöscht → Begriffe weg.
        b.HasOne<Library>()
            .WithMany()
            .HasForeignKey(x => x.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        // „Hund“ und „hund“ sind in einer Bibliothek derselbe Begriff (NOCASE-Spalte).
        b.HasIndex(x => new { x.LibraryId, x.Name }).IsUnique().HasDatabaseName("UX_MotifTerms_Library_Name");
    }
}

internal sealed class FileEmbeddingConfiguration : IEntityTypeConfiguration<FileEmbedding>
{
    public void Configure(EntityTypeBuilder<FileEmbedding> b)
    {
        b.ToTable("FileEmbeddings");
        b.HasKey(x => x.FileId);
        b.Property(x => x.FileId).ValueGeneratedNever();
        b.Property(x => x.ModelVersion).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        b.HasOne<MediaFile>()
            .WithOne()
            .HasForeignKey<FileEmbedding>(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Zählen „analysiert mit Version X“ je Bibliothek ohne die Datensätze mit dem 3-KB-Vektor zu lesen (abdeckend).
        b.HasIndex(x => new { x.FileId, x.ModelVersion }).HasDatabaseName("IX_FileEmbeddings_File_Model");
    }
}

internal sealed class FileMotifConfiguration : IEntityTypeConfiguration<FileMotif>
{
    public void Configure(EntityTypeBuilder<FileMotif> b)
    {
        b.ToTable("FileMotifs");
        b.HasKey(x => new { x.FileId, x.TermId });

        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<MotifTerm>()
            .WithMany()
            .HasForeignKey(x => x.TermId)
            .OnDelete(DeleteBehavior.Cascade);

        // Filter „Motiv = Hund“ und Zähler je Begriff über den Index statt über alle Treffer.
        b.HasIndex(x => new { x.TermId, x.FileId }).HasDatabaseName("IX_FileMotifs_Term_File");
    }
}
