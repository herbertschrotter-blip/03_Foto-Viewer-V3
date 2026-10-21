using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

// Gesichtserkennung (ADR-0035, Migration AddFaces). Fremdschlüssel ohne Navigationen (ADR-0004); e_sqlite3 erzwingt
// sie (SQLITE_DEFAULT_FOREIGN_KEYS=1, siehe FileTagConfiguration).

internal sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> b)
    {
        b.ToTable("Persons");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).IsRequired().UseCollation("NOCASE");
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.IsHidden).IsRequired();

        // CoverFaceId bewusst OHNE Fremdschlüssel: Persons → Faces und Faces → Persons wären ein Zyklus.
        b.Property(x => x.CoverFaceId);
    }
}

internal sealed class FaceConfiguration : IEntityTypeConfiguration<Face>
{
    public void Configure(EntityTypeBuilder<Face> b)
    {
        b.ToTable("Faces");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Landmarks).IsRequired();
        b.Property(x => x.Embedding).IsRequired();
        b.Property(x => x.ModelVersion).IsRequired();
        b.Property(x => x.State).HasConversion<int>().IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // Files-Zeile weg (Papierkorb, Wurzel entfernt, „Neu aufbauen“) → Gesicht weg. „Neu aufbauen“ trägt die
        // Gesichter vorher über UserDataCarryOver auf die neuen FileIds (ADR-0035).
        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Person gelöscht → Zuordnung und Vorschlag fallen weg, das Gesicht bleibt.
        b.HasOne<Person>()
            .WithMany()
            .HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Person>()
            .WithMany()
            .HasForeignKey(x => x.SuggestedPersonId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => x.FileId).HasDatabaseName("IX_Faces_File");
        b.HasIndex(x => x.PersonId).HasDatabaseName("IX_Faces_Person");
        b.HasIndex(x => x.State).HasDatabaseName("IX_Faces_State");
        b.HasIndex(x => x.SuggestedPersonId).HasDatabaseName("IX_Faces_SuggestedPerson");

        // Personen in Galerie, Suche, Liste (0.2.38, Migration AddFacePersonIndex): abdeckende Indizes, weil State,
        // PersonId und SuggestedPersonId im Datensatz HINTER dem 2-KB-Fingerabdruck liegen — ohne sie liest jede
        // Prüfung die Überlaufseiten des Fingerabdrucks.
        b.HasIndex(x => new { x.PersonId, x.State, x.FileId }).HasDatabaseName("IX_Faces_Person_State_File");
        b.HasIndex(x => new { x.SuggestedPersonId, x.State, x.FileId }).HasDatabaseName("IX_Faces_SuggestedPerson_State_File");
        b.HasIndex(x => new { x.FileId, x.State, x.PersonId, x.SuggestedPersonId }).HasDatabaseName("IX_Faces_File_State_Person");
    }
}

internal sealed class FaceRejectionConfiguration : IEntityTypeConfiguration<FaceRejection>
{
    public void Configure(EntityTypeBuilder<FaceRejection> b)
    {
        b.ToTable("FaceRejections");
        b.HasKey(x => new { x.FaceId, x.PersonId });

        b.HasOne<Face>()
            .WithMany()
            .HasForeignKey(x => x.FaceId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Person>()
            .WithMany()
            .HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.PersonId).HasDatabaseName("IX_FaceRejections_Person");
    }
}

internal sealed class FaceScanConfiguration : IEntityTypeConfiguration<FaceScan>
{
    public void Configure(EntityTypeBuilder<FaceScan> b)
    {
        b.ToTable("FaceScans");
        b.HasKey(x => x.FileId);
        b.Property(x => x.FileId).ValueGeneratedNever();
        b.Property(x => x.ModelVersion).IsRequired();
        b.Property(x => x.ScannedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        b.HasOne<MediaFile>()
            .WithOne()
            .HasForeignKey<FaceScan>(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
