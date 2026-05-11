using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

internal sealed class MediaFileConfiguration : IEntityTypeConfiguration<MediaFile>
{
    public void Configure(EntityTypeBuilder<MediaFile> b)
    {
        b.ToTable("Files");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.LibraryId).IsRequired();
        b.Property(x => x.FolderId).IsRequired();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Extension).IsRequired();
        b.Property(x => x.Size).IsRequired();
        b.Property(x => x.MediaType).HasConversion<int>();
        b.Property(x => x.IsFavorite).IsRequired();
        b.Property(x => x.Missing).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.ModifiedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // V0.1-Pflicht-Indizes laut PLAN Kap. 4
        b.HasIndex(x => new { x.LibraryId, x.FolderId })
            .HasDatabaseName("IX_Files_Library_Folder");
        b.HasIndex(x => new { x.FolderId, x.Name })
            .HasDatabaseName("IX_Files_Folder_Name");
        b.HasIndex(x => new { x.LibraryId, x.MediaType })
            .HasDatabaseName("IX_Files_Library_MediaType");
        b.HasIndex(x => new { x.LibraryId, x.ModifiedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_Files_Library_Modified");
        b.HasIndex(x => new { x.LibraryId, x.IsFavorite })
            .HasDatabaseName("IX_Files_Library_Favorite");
        b.HasIndex(x => new { x.FolderId, x.Name, x.Size, x.ModifiedAtUtc })
            .IsUnique()
            .HasDatabaseName("UX_Files_Folder_Name_Size_Modified");
    }
}
