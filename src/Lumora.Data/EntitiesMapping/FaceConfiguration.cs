using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

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

        // Personen-Details (0.2.47, ADR-0045, Migration AddPersonDetails). Name bleibt der synchron gehaltene Anzeigename.
        b.Property(x => x.FirstName).IsRequired().HasDefaultValue(string.Empty).UseCollation("NOCASE");
        b.Property(x => x.LastName).IsRequired().HasDefaultValue(string.Empty).UseCollation("NOCASE");
        b.Property(x => x.Nickname).UseCollation("NOCASE");
        b.Property(x => x.IsMe).IsRequired().HasDefaultValue(false);
        b.HasOne<PersonRelation>()
            .WithMany()
            .HasForeignKey(x => x.RelationId)
            .OnDelete(DeleteBehavior.SetNull); // Beziehung gelöscht → Person behält leere Beziehung
        b.HasIndex(x => x.RelationId).HasDatabaseName("IX_Persons_Relation");
        // Bibliotheks-Gruppe (0.2.51, ADR-0048, ohne Fremdschlüssel). Höchstens eine Person „Das bin ich“ JE GRUPPE
        // (Teilindex nur über IsMe = 1); der Anzeigename ist je Gruppe eindeutig (im Code geprüft, Index für die Suche).
        b.Property(x => x.GroupId).IsRequired();
        b.HasIndex(x => new { x.GroupId, x.Name }).HasDatabaseName("IX_Persons_Group_Name");
        b.HasIndex(x => new { x.GroupId, x.IsMe }).IsUnique().HasFilter("\"IsMe\" = 1").HasDatabaseName("IX_Persons_Group_IsMe");
    }
}

internal sealed class PersonRelationConfiguration : IEntityTypeConfiguration<PersonRelation>
{
    public void Configure(EntityTypeBuilder<PersonRelation> b)
    {
        b.ToTable("PersonRelations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).IsRequired().UseCollation("NOCASE");
        // NOCASE: „schwester“ = „Schwester“ — seit 0.2.51 je Bibliotheks-Gruppe (ADR-0048).
        b.Property(x => x.GroupId).IsRequired();
        b.HasIndex(x => new { x.GroupId, x.Name }).IsUnique().HasDatabaseName("IX_PersonRelations_Group_Name");
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
