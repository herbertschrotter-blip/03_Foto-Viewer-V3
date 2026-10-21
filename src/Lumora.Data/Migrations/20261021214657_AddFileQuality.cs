using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Foto-Güte (ADR-0057): neue Tabelle FileQuality (Schärfe, Belichtung, Bildwirkung je Datei, gültig für Größe und
    /// Änderungszeit der gelesenen Dateiversion). Nur CREATE TABLE — bestehende Tabellen bleiben unverändert; die Werte
    /// entstehen im Hintergrund.
    /// </summary>
    public partial class AddFileQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileQuality",
                columns: table => new
                {
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Sharpness = table.Column<double>(type: "REAL", nullable: true),
                    Exposure = table.Column<double>(type: "REAL", nullable: true),
                    Aesthetic = table.Column<double>(type: "REAL", nullable: true),
                    AestheticVersion = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSize = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceModifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileQuality", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_FileQuality_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileQuality");
        }
    }
}
