using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

internal sealed class LibraryConfiguration : IEntityTypeConfiguration<Library>
{
    public void Configure(EntityTypeBuilder<Library> b)
    {
        b.ToTable("Libraries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.RootPath).IsRequired();
        b.Property(x => x.CreatedAt).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.LastOpenedAt).HasConversion(ValueConverters.NullableDateTimeToUnixSeconds);

        // Verknüpfungsgruppe (ADR-0048, Migration AddLibraryScoping). Bewusst OHNE Fremdschlüssel: ein FK ließe EF die
        // Tabelle Libraries beim Anlegen neu aufbauen; die Gruppen pflegt allein LibraryGroupStore.
        b.Property(x => x.GroupId).IsRequired();
        b.HasIndex(x => x.GroupId).HasDatabaseName("IX_Libraries_Group");
    }
}

internal sealed class LibraryGroupConfiguration : IEntityTypeConfiguration<LibraryGroup>
{
    public void Configure(EntityTypeBuilder<LibraryGroup> b)
    {
        b.ToTable("LibraryGroups");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).IsRequired().UseCollation("NOCASE");
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
    }
}
