using Microsoft.EntityFrameworkCore.Migrations;
using Lumora.Data.RawSql;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Aufnahmedatum (ADR-0046): Files.CapturedAt/-Source/-Precision/-Offset (bestehende Zeilen: NULL = noch nicht gelesen,
    /// das Nachtragen im Hintergrund füllt sie), Tabelle CaptureDateUndo und der Ausdrucks-Index für die Sortierung
    /// (Raw-SQL, EF kennt keine Ausdrucks-Indizes).
    /// </summary>
    public partial class AddCapturedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CapturedAt",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CapturedAtOffset",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CapturedAtPrecision",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CapturedAtSource",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaptureDateUndo",
                columns: table => new
                {
                    FileOperationId = table.Column<int>(type: "INTEGER", nullable: false),
                    OldCapturedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    OldSource = table.Column<int>(type: "INTEGER", nullable: true),
                    OldPrecision = table.Column<int>(type: "INTEGER", nullable: false),
                    OldOffset = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureDateUndo", x => x.FileOperationId);
                    table.ForeignKey(
                        name: "FK_CaptureDateUndo_FileOperations_FileOperationId",
                        column: x => x.FileOperationId,
                        principalTable: "FileOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(CaptureDateSortSql.CreateIndexSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CaptureDateSortSql.DropIndexSql);

            migrationBuilder.DropTable(
                name: "CaptureDateUndo");

            migrationBuilder.DropColumn(
                name: "CapturedAt",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "CapturedAtOffset",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "CapturedAtPrecision",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "CapturedAtSource",
                table: "Files");
        }
    }
}
