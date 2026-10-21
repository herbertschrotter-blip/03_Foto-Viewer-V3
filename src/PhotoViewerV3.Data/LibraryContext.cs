using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Domain.Entities;

namespace PhotoViewerV3.Data;

/// <summary>
/// Zentraler EF Core DbContext für die Library-DB (siehe ADR-0003 + ADR-0004).
/// CRUD-Pfad. Bulk-Insert (Scanner) und FTS5 laufen über Raw Microsoft.Data.Sqlite
/// in PhotoViewerV3.Data.RawSql/ — parallel zum DbContext, gleiche Connection-String.
/// </summary>
public sealed class LibraryContext : DbContext
{
    public LibraryContext(DbContextOptions<LibraryContext> options)
        : base(options)
    {
    }

    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<LibraryGroup> LibraryGroups => Set<LibraryGroup>(); // ADR-0048
    public DbSet<LibraryRoot> LibraryRoots => Set<LibraryRoot>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<MediaFile> Files => Set<MediaFile>();
    public DbSet<Thumbnail> Thumbnails => Set<Thumbnail>();
    public DbSet<OperationBatch> OperationBatches => Set<OperationBatch>();
    public DbSet<FileOperation> FileOperations => Set<FileOperation>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<FileTag> FileTags => Set<FileTag>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryValue> CategoryValues => Set<CategoryValue>();
    public DbSet<FileCategoryValue> FileCategoryValues => Set<FileCategoryValue>();
    public DbSet<Person> Persons => Set<Person>();
    public DbSet<PersonRelation> PersonRelations => Set<PersonRelation>(); // ADR-0045
    public DbSet<Face> Faces => Set<Face>();
    public DbSet<FaceRejection> FaceRejections => Set<FaceRejection>();
    public DbSet<FaceScan> FaceScans => Set<FaceScan>();
    public DbSet<MotifTerm> MotifTerms => Set<MotifTerm>(); // Motive (ADR-0052)
    public DbSet<FileEmbedding> FileEmbeddings => Set<FileEmbedding>();
    public DbSet<FileMotif> FileMotifs => Set<FileMotif>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LibraryContext).Assembly);
    }
}
