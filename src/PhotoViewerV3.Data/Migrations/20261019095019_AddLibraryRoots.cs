using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoViewerV3.Data.Migrations
{
    /// <summary>
    /// ADR-0020: Bibliothek mit mehreren Wurzeln und Rollen. Neue Tabelle LibraryRoots, Spalte Folders.RootId
    /// (logischer FK ohne Constraint — wie alle Verweise des Schemas, erspart den SQLite-Tabellenneuaufbau).
    /// Daten-Migration (ADR-0005, Bestand bleibt): jede bestehende Libraries.RootPath wird als Archiv-Wurzel
    /// übernommen (Name = letztes Pfadsegment), bestehende Folders-Zeilen bekommen per Pfad-Präfix ihren RootId.
    /// </summary>
    public partial class AddLibraryRoots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RootId",
                table: "Folders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LibraryRoots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryRoots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_Root",
                table: "Folders",
                column: "RootId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryRoots_Library_Role",
                table: "LibraryRoots",
                columns: new[] { "LibraryId", "Role" });

            migrationBuilder.CreateIndex(
                name: "UX_LibraryRoots_Library_Path",
                table: "LibraryRoots",
                columns: new[] { "LibraryId", "Path" },
                unique: true);

            // Bestandsdaten: RootPath jeder Bibliothek → eine Archiv-Wurzel (Role 1). Der Name ist das letzte
            // Pfadsegment (SQLite-Idiom: alles nach dem letzten Backslash), bei Laufwerkswurzeln „C:“.
            migrationBuilder.Sql("""
                INSERT INTO LibraryRoots (LibraryId, Path, Role, Name, SortOrder)
                SELECT Id,
                       RootPath,
                       1,
                       CASE
                           WHEN instr(rtrim(RootPath, '\'), '\') = 0 THEN rtrim(RootPath, '\')
                           ELSE replace(rtrim(RootPath, '\'), rtrim(rtrim(RootPath, '\'), replace(rtrim(RootPath, '\'), '\', '')), '')
                       END,
                       0
                FROM Libraries;
                """);

            // Ordnerzeilen an ihre Wurzel hängen: Pfad gleich der Wurzel (mit/ohne Trennzeichen) oder darunter.
            // Vergleich über lower() (ASCII-Faltung wie NTFS für A–Z; derselbe Vergleich wie im FileOperationService).
            migrationBuilder.Sql("""
                UPDATE Folders
                SET RootId = (
                    SELECT r.Id FROM LibraryRoots r
                    WHERE r.LibraryId = Folders.LibraryId
                      AND (lower(rtrim(Folders.Path, '\')) = lower(rtrim(r.Path, '\'))
                           OR lower(substr(Folders.Path, 1, length(rtrim(r.Path, '\')) + 1)) = lower(rtrim(r.Path, '\') || '\'))
                    ORDER BY length(r.Path) DESC
                    LIMIT 1)
                WHERE RootId IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryRoots");

            migrationBuilder.DropIndex(
                name: "IX_Folders_Root",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "RootId",
                table: "Folders");
        }
    }
}
