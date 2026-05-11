using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

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
    }
}
