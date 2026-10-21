using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

// Storys (ADR-0058, Migration AddStories). Nur neue Tabellen — kein Neuaufbau bestehender (FK-Kaskaden-Lehre aus
// ADR-0045/0048). Fremdschlüssel ohne Navigationen (ADR-0004), von e_sqlite3 erzwungen.

internal sealed class StoryConfiguration : IEntityTypeConfiguration<Story>
{
    public void Configure(EntityTypeBuilder<Story> b)
    {
        b.ToTable("Stories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Kind).HasConversion<int>();
        b.Property(x => x.Signature).IsRequired();
        b.Property(x => x.DatePrecision).HasConversion<int>();
        b.Property(x => x.StartAt).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.EndAt).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.PinnedAtUtc).HasConversion(ValueConverters.NullableDateTimeToUnixSeconds);
        b.Property(x => x.SeenAtUtc).HasConversion(ValueConverters.NullableDateTimeToUnixSeconds);
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.UpdatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // Je Bibliotheks-Gruppe eindeutig; GroupId ohne Fremdschlüssel wie Alben (ADR-0048/0054).
        b.Property(x => x.GroupId).IsRequired();
        b.HasIndex(x => new { x.GroupId, x.Signature }).IsUnique().HasDatabaseName("UX_Stories_Group_Signature");
        b.HasIndex(x => new { x.GroupId, x.Kind, x.StartAt }).HasDatabaseName("IX_Stories_Group_Kind_Start");

        // Titelbild: Datei weg → die Erkennung wählt ein neues.
        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.CoverFileId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.CoverFileId).HasDatabaseName("IX_Stories_CoverFile");

        // Personen-Story: Person gelöscht → Story weg.
        b.HasOne<Person>()
            .WithMany()
            .HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.PersonId).HasDatabaseName("IX_Stories_Person");
    }
}

internal sealed class StoryItemConfiguration : IEntityTypeConfiguration<StoryItem>
{
    public void Configure(EntityTypeBuilder<StoryItem> b)
    {
        b.ToTable("StoryItems");
        // Schlüssel (StoryId, FileId): „Dateien der Story“ liest den Primärschlüssel-Index.
        b.HasKey(x => new { x.StoryId, x.FileId });
        b.Property(x => x.Origin).HasConversion<int>();
        b.Property(x => x.AddedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        b.HasOne<Story>()
            .WithMany()
            .HasForeignKey(x => x.StoryId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Kaskade beim Löschen einer Files-Zeile und „Storys dieser Datei“ über den Index.
        b.HasIndex(x => x.FileId).HasDatabaseName("IX_StoryItems_File");
    }
}
