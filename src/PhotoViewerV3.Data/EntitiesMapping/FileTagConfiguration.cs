using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data.EntitiesMapping;

internal sealed class FileTagConfiguration : IEntityTypeConfiguration<FileTag>
{
    public void Configure(EntityTypeBuilder<FileTag> b)
    {
        b.ToTable("FileTags");
        b.HasKey(x => new { x.FileId, x.TagId });

        // Fremdschlüssel ohne Navigationseigenschaften (Entities bleiben flach, ADR-0004); ON DELETE CASCADE,
        // damit Papierkorb/Neu aufbauen (Files) und Tag löschen (Tags) die Zuweisungen in der DB mitnehmen.
        // SQLite erzwingt Fremdschlüssel nur mit PRAGMA foreign_keys=ON — e_sqlite3 (SQLitePCLRaw) ist mit
        // SQLITE_DEFAULT_FOREIGN_KEYS=1 gebaut; ein Test in TagServiceTests prüft das.
        b.HasOne<MediaFile>()
            .WithMany()
            .HasForeignKey(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Tag>()
            .WithMany()
            .HasForeignKey(x => x.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.TagId)
            .HasDatabaseName("IX_FileTags_Tag");
    }
}
