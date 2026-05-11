using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoViewerV3.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialV01 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: true),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: false),
                    DestPath = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    UndoneAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    FolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Extension = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Codec = table.Column<string>(type: "TEXT", nullable: true),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    Rating = table.Column<byte>(type: "INTEGER", nullable: true),
                    Missing = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Folders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    ParentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ScannedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Libraries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    RootPath = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastOpenedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Libraries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OperationType = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    PlanId = table.Column<int>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Thumbnails",
                columns: table => new
                {
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Size = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    CachePath = table.Column<string>(type: "TEXT", nullable: false),
                    SourceModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    GeneratedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Thumbnails", x => new { x.FileId, x.Size, x.Kind });
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileOperations_Batch_Status",
                table: "FileOperations",
                columns: new[] { "BatchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Folder_Name",
                table: "Files",
                columns: new[] { "FolderId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Library_Favorite",
                table: "Files",
                columns: new[] { "LibraryId", "IsFavorite" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Library_Folder",
                table: "Files",
                columns: new[] { "LibraryId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Library_MediaType",
                table: "Files",
                columns: new[] { "LibraryId", "MediaType" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Library_Modified",
                table: "Files",
                columns: new[] { "LibraryId", "ModifiedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "UX_Files_Folder_Name_Size_Modified",
                table: "Files",
                columns: new[] { "FolderId", "Name", "Size", "ModifiedAtUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Folders_Library_Path",
                table: "Folders",
                columns: new[] { "LibraryId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Thumbnails_File_Size_Kind",
                table: "Thumbnails",
                columns: new[] { "FileId", "Size", "Kind" });

            // FTS5 Virtual Table (V0.1 minimal: Name + Extension)
            // Wird von EF Core nicht direkt unterstützt → Raw SQL.
            // External-Content-Pattern: content='Files', content_rowid='Id'.
            // Befüllung erfolgt nach Bulk-Insert via INSERT INTO FilesFts(rowid,...) SELECT ... FROM Files
            // (siehe Spike #1) — KEINE Trigger im MVP, um Bulk-Insert nicht zu verlangsamen.
            migrationBuilder.Sql(@"
                CREATE VIRTUAL TABLE FilesFts USING fts5(
                    Name,
                    Extension,
                    content='Files',
                    content_rowid='Id'
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // FTS5 zuerst, weil sie als Virtual Table auf Files referenziert
            migrationBuilder.Sql("DROP TABLE IF EXISTS FilesFts;");

            migrationBuilder.DropTable(
                name: "FileOperations");

            migrationBuilder.DropTable(
                name: "Files");

            migrationBuilder.DropTable(
                name: "Folders");

            migrationBuilder.DropTable(
                name: "Libraries");

            migrationBuilder.DropTable(
                name: "OperationBatches");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Thumbnails");
        }
    }
}
