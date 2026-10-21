using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoViewerV3.Data.Migrations
{
    /// <summary>
    /// Doppelte erkennen (ADR-0053): neue Tabellen FileFingerprints (SHA-256 und 64-Bit-pHash je Datei, gefilterter Index
    /// über die Prüfsumme) und DuplicateCleanupUndo (Rückgängig-Daten zu „Übrige in den Papierkorb“). Nur CREATE TABLE —
    /// bestehende Tabellen bleiben unverändert; die Fingerabdrücke entstehen im Hintergrund.
    /// </summary>
    public partial class AddDuplicateDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DuplicateCleanupUndo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    KeptFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Snapshot = table.Column<string>(type: "TEXT", nullable: false),
                    Transfer = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UndoneAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateCleanupUndo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DuplicateCleanupUndo_OperationBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "OperationBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileFingerprints",
                columns: table => new
                {
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ContentSize = table.Column<long>(type: "INTEGER", nullable: true),
                    ContentModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ImageHash = table.Column<long>(type: "INTEGER", nullable: true),
                    ImageHashVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    ImageModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ImageLowDetail = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ImageError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileFingerprints", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_FileFingerprints_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateCleanupUndo_Batch",
                table: "DuplicateCleanupUndo",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_FileFingerprints_ContentHash",
                table: "FileFingerprints",
                column: "ContentHash",
                filter: "ContentHash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DuplicateCleanupUndo");

            migrationBuilder.DropTable(
                name: "FileFingerprints");
        }
    }
}
