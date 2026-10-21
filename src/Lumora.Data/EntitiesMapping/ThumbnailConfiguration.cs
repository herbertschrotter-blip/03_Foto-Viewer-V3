using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lumora.Domain.Entities;

namespace Lumora.Data.EntitiesMapping;

internal sealed class ThumbnailConfiguration : IEntityTypeConfiguration<Thumbnail>
{
    public void Configure(EntityTypeBuilder<Thumbnail> b)
    {
        b.ToTable("Thumbnails");
        // Composite-Key: ein Thumbnail-Eintrag pro (File, Size, Kind)
        b.HasKey(x => new { x.FileId, x.Size, x.Kind });
        b.Property(x => x.Kind).HasConversion<int>();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.Format).IsRequired();
        b.Property(x => x.CachePath).IsRequired();
        b.Property(x => x.SourceModifiedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.GeneratedAtUtc).HasConversion(ValueConverters.NullableDateTimeToUnixSeconds);
        b.Property(x => x.LastAttemptAtUtc).HasConversion(ValueConverters.NullableDateTimeToUnixSeconds);

        b.HasIndex(x => new { x.FileId, x.Size, x.Kind })
            .HasDatabaseName("IX_Thumbnails_File_Size_Kind");
    }
}
