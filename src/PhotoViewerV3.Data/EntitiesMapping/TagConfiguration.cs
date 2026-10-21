using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> b)
    {
        b.ToTable("Tags");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        // NOCASE: „Urlaub“ und „urlaub“ sind dasselbe Tag (eindeutiger Index greift ohne Groß-/Kleinschreibung).
        b.Property(x => x.Name).IsRequired().UseCollation("NOCASE");
        b.Property(x => x.Color).IsRequired();
        b.Property(x => x.SortOrder).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // Seit 0.2.51 (ADR-0048) je Bibliotheks-Gruppe eindeutig; GroupId ohne Fremdschlüssel (siehe LibraryConfiguration).
        b.Property(x => x.GroupId).IsRequired();
        b.HasIndex(x => new { x.GroupId, x.Name })
            .IsUnique()
            .HasDatabaseName("UX_Tags_Group_Name");
    }
}
