using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Orte (ADR-0050): Files.Latitude/Longitude/GeoSource/PlaceId (bestehende Zeilen: GeoSource NULL = noch nicht gelesen,
    /// das Nachtragen im Hintergrund füllt sie), Tabelle GeoTagUndo und die gefilterten Indizes für Karte und Ortsfilter.
    /// </summary>
    public partial class AddGeoLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GeoSource",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "Files",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "Files",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlaceId",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GeoTagUndo",
                columns: table => new
                {
                    FileOperationId = table.Column<int>(type: "INTEGER", nullable: false),
                    OldLatitude = table.Column<double>(type: "REAL", nullable: true),
                    OldLongitude = table.Column<double>(type: "REAL", nullable: true),
                    OldGeoSource = table.Column<int>(type: "INTEGER", nullable: true),
                    OldPlaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    NewLatitude = table.Column<double>(type: "REAL", nullable: true),
                    NewLongitude = table.Column<double>(type: "REAL", nullable: true),
                    WriteKind = table.Column<int>(type: "INTEGER", nullable: false),
                    WrittenPath = table.Column<string>(type: "TEXT", nullable: true),
                    OldBytes = table.Column<byte[]>(type: "BLOB", nullable: true),
                    WrittenHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SegmentOffset = table.Column<long>(type: "INTEGER", nullable: true),
                    OldModifiedTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    OldSize = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeoTagUndo", x => x.FileOperationId);
                    table.ForeignKey(
                        name: "FK_GeoTagUndo_FileOperations_FileOperationId",
                        column: x => x.FileOperationId,
                        principalTable: "FileOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Library_Geo",
                table: "Files",
                columns: new[] { "LibraryId", "Latitude", "Longitude" },
                filter: "Latitude IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Files_Library_Place",
                table: "Files",
                columns: new[] { "LibraryId", "PlaceId" },
                filter: "PlaceId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GeoTagUndo");

            migrationBuilder.DropIndex(
                name: "IX_Files_Library_Geo",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_Library_Place",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "GeoSource",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PlaceId",
                table: "Files");
        }
    }
}
