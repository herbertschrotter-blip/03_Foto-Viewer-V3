using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

internal sealed class LibraryRootConfiguration : IEntityTypeConfiguration<LibraryRoot>
{
    public void Configure(EntityTypeBuilder<LibraryRoot> b)
    {
        b.ToTable("LibraryRoots");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.LibraryId).IsRequired();
        b.Property(x => x.Path).IsRequired();
        b.Property(x => x.Role).HasConversion<int>();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.SortOrder).IsRequired();

        b.HasIndex(x => new { x.LibraryId, x.Path })
            .IsUnique()
            .HasDatabaseName("UX_LibraryRoots_Library_Path");
        b.HasIndex(x => new { x.LibraryId, x.Role })
            .HasDatabaseName("IX_LibraryRoots_Library_Role");
    }
}
