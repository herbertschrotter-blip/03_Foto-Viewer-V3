using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

// Alben und Ordner-Verknüpfungen (ADR-0054, Migration AddAlbums). Nur neue Tabellen — kein Neuaufbau bestehender
// (FK-Kaskaden-Lehre aus ADR-0045/0048). Fremdschlüssel ohne Navigationen (ADR-0004), von e_sqlite3 erzwungen.

internal sealed class AlbumConfiguration : IEntityTypeConfiguration<Album>
{
    public void Configure(EntityTypeBuilder<Album> b)
    {
        b.ToTable("Albums");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        // NOCASE: „Urlaub 2024“ und „urlaub 2024“ sind in einer Gruppe dasselbe Album.
        b.Property(x => x.Name).IsRequired().UseCollation("NOCASE");
        b.Property(x => x.Kind).HasConversion<int>();
        b.Property(x => x.SortOrder).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.UpdatedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        // Je Bibliotheks-Gruppe eindeutig; GroupId ohne Fremdschlüssel wie Tags/Kategorien (ADR-0048).
        b.Property(x => x.GroupId).IsRequired();
        b.HasIndex(x => new { x.GroupId, x.Name }).IsUnique().HasDatabaseName("UX_Albums_Group_Name");

        // Titelbild: Datei weg → Album nimmt wieder das neueste Foto.
        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.CoverFileId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.CoverFileId).HasDatabaseName("IX_Albums_CoverFile");
    }
}

internal sealed class AlbumItemConfiguration : IEntityTypeConfiguration<AlbumItem>
{
    public void Configure(EntityTypeBuilder<AlbumItem> b)
    {
        b.ToTable("AlbumItems");
        // Schlüssel (AlbumId, FileId): „Fotos des Albums“ liest den Primärschlüssel-Index.
        b.HasKey(x => new { x.AlbumId, x.FileId });
        b.Property(x => x.AddedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        b.HasOne<Album>()
            .WithMany()
            .HasForeignKey(x => x.AlbumId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Kaskade beim Löschen einer Files-Zeile und „Alben dieser Datei“ (Kontextmenü) über den Index.
        b.HasIndex(x => x.FileId).HasDatabaseName("IX_AlbumItems_File");
    }
}

internal sealed class FolderLinkConfiguration : IEntityTypeConfiguration<FolderLink>
{
    public void Configure(EntityTypeBuilder<FolderLink> b)
    {
        b.ToTable("FolderLinks");
        b.HasKey(x => new { x.FolderId, x.FileId });
        b.Property(x => x.AddedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);

        b.HasOne<Folder>()
            .WithMany()
            .HasForeignKey(x => x.FolderId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.FileId).HasDatabaseName("IX_FolderLinks_File");
    }
}

internal sealed class FileLinkUndoConfiguration : IEntityTypeConfiguration<FileLinkUndo>
{
    public void Configure(EntityTypeBuilder<FileLinkUndo> b)
    {
        b.ToTable("FileLinkUndo");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Kind).HasConversion<int>();
        b.Property(x => x.AddedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.Property(x => x.SourcePath).IsRequired();
        b.Property(x => x.FileModifiedAtUtc).HasConversion(ValueConverters.DateTimeToUnixSeconds);
        b.HasOne<FileOperation>()
            .WithMany()
            .HasForeignKey(x => x.FileOperationId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.FileOperationId).HasDatabaseName("IX_FileLinkUndo_Operation");
    }
}
