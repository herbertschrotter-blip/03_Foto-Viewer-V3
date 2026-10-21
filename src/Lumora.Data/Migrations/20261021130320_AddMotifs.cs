using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Motive (ADR-0052): Tabellen MotifTerms (Begriffe je Bibliothek mit Text-Vektor), FileEmbeddings (Bild-Vektor je
    /// Datei, Vektor als letzte Spalte) und FileMotifs (Treffer über der Schwelle). Bestehende Daten bleiben; die
    /// Standard-Begriffe legt der Dienst beim ersten Start je Bibliothek an, die Bild-Vektoren entstehen im Hintergrund.
    /// </summary>
    public partial class AddMotifs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileEmbeddings",
                columns: table => new
                {
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Embedding = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileEmbeddings", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_FileEmbeddings_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MotifTerms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Prompt = table.Column<string>(type: "TEXT", nullable: true),
                    Threshold = table.Column<double>(type: "REAL", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    TextEmbedding = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TextModelVersion = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MotifTerms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MotifTerms_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileMotifs",
                columns: table => new
                {
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    TermId = table.Column<int>(type: "INTEGER", nullable: false),
                    Score = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileMotifs", x => new { x.FileId, x.TermId });
                    table.ForeignKey(
                        name: "FK_FileMotifs_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FileMotifs_MotifTerms_TermId",
                        column: x => x.TermId,
                        principalTable: "MotifTerms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileEmbeddings_File_Model",
                table: "FileEmbeddings",
                columns: new[] { "FileId", "ModelVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_FileMotifs_Term_File",
                table: "FileMotifs",
                columns: new[] { "TermId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "UX_MotifTerms_Library_Name",
                table: "MotifTerms",
                columns: new[] { "LibraryId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileEmbeddings");

            migrationBuilder.DropTable(
                name: "FileMotifs");

            migrationBuilder.DropTable(
                name: "MotifTerms");
        }
    }
}
