using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Ungenaue Orte und GPS-Spuren (ADR-0062): nur ADD COLUMN an <c>Files</c> (Genauigkeit, Grund, GPS-Zeit, Abstand aus
    /// OffsetTimeOriginal, Kamera, abgelehnter Vorschlag) und die neue Tabelle <c>GpsTracks</c> — kein Neuaufbau bestehender
    /// Tabellen; das Nachtragen im Hintergrund füllt die Spalten.
    /// </summary>
    public partial class AddGpsTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CameraModel",
                table: "Files",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExifTimeOffset",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GeoAccuracy",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GeoAccuracyReason",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GeoSuggestionState",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "GpsTimeUtc",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GpsTracks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EndUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    MinLatitude = table.Column<double>(type: "REAL", nullable: false),
                    MinLongitude = table.Column<double>(type: "REAL", nullable: false),
                    MaxLatitude = table.Column<double>(type: "REAL", nullable: false),
                    MaxLongitude = table.Column<double>(type: "REAL", nullable: false),
                    PointCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DistanceMeters = table.Column<double>(type: "REAL", nullable: false),
                    Sport = table.Column<string>(type: "TEXT", nullable: true),
                    Device = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Checksum = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Points = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ImportedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpsTracks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Library_GeoEstimated",
                table: "Files",
                columns: new[] { "LibraryId", "GeoAccuracy" },
                filter: "GeoAccuracy = 2");

            migrationBuilder.CreateIndex(
                name: "IX_GpsTracks_Group_Time",
                table: "GpsTracks",
                columns: new[] { "GroupId", "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_GpsTracks_Group_Checksum",
                table: "GpsTracks",
                columns: new[] { "GroupId", "Checksum" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GpsTracks");

            migrationBuilder.DropIndex(
                name: "IX_Files_Library_GeoEstimated",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "CameraModel",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "ExifTimeOffset",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "GeoAccuracy",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "GeoAccuracyReason",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "GeoSuggestionState",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "GpsTimeUtc",
                table: "Files");
        }
    }
}
