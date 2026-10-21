using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("Categories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        // NOCASE wie bei Tags: „Projekt“ und „projekt“ sind dieselbe Kategorie.
        b.Property(x => x.Name).IsRequired().UseCollation("NOCASE");
        b.Property(x => x.SortOrder).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // Seit 0.2.51 (ADR-0048) je Bibliotheks-Gruppe eindeutig; GroupId ohne Fremdschlüssel (siehe LibraryConfiguration).
        b.Property(x => x.GroupId).IsRequired();
        b.HasIndex(x => new { x.GroupId, x.Name })
            .IsUnique()
            .HasDatabaseName("UX_Categories_Group_Name");
    }
}

internal sealed class CategoryValueConfiguration : IEntityTypeConfiguration<CategoryValue>
{
    public void Configure(EntityTypeBuilder<CategoryValue> b)
    {
        b.ToTable("CategoryValues");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).IsRequired().UseCollation("NOCASE");
        b.Property(x => x.SortOrder).IsRequired();

        b.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.CategoryId, x.Name })
            .IsUnique()
            .HasDatabaseName("UX_CategoryValues_Category_Name");
    }
}

internal sealed class FileCategoryValueConfiguration : IEntityTypeConfiguration<FileCategoryValue>
{
    public void Configure(EntityTypeBuilder<FileCategoryValue> b)
    {
        b.ToTable("FileCategoryValues");
        b.HasKey(x => new { x.FileId, x.CategoryId });

        // Fremdschlüssel ohne Navigationen (ADR-0004), alle ON DELETE CASCADE (siehe FileTagConfiguration).
        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<CategoryValue>()
            .WithMany()
            .HasForeignKey(x => x.ValueId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.ValueId)
            .HasDatabaseName("IX_FileCategoryValues_Value");
        // Ersetzt seit 0.2.30 IX_FileCategoryValues_Category (ADR-0031): Bereich einer Kategorie nach Wert und FileId
        // geordnet und abdeckend — Segment-Zählung der Kategorie-Sortierung, Kategorie-Filter, Suche nach Kategorie-Namen
        // und „ohne Wert“ lesen nur den Index. Deckt als Präfix auch den Fremdschlüssel CategoryId ab.
        b.HasIndex(x => new { x.CategoryId, x.ValueId, x.FileId })
            .HasDatabaseName("IX_FileCategoryValues_Category_Value_File");
    }
}
