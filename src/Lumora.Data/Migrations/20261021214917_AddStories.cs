using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Storys (ADR-0058): nur neue Tabellen (<c>Stories</c> je Bibliotheks-Gruppe, <c>StoryItems</c>) — kein Neuaufbau
    /// bestehender Tabellen, keine Datenübernahme; die Erkennung füllt sie im Hintergrund.
    /// </summary>
    public partial class AddStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Stories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Signature = table.Column<string>(type: "TEXT", nullable: false),
                    StartAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstDay = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDay = table.Column<int>(type: "INTEGER", nullable: false),
                    DatePrecision = table.Column<int>(type: "INTEGER", nullable: false),
                    PlaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: true),
                    PlaceIds = table.Column<string>(type: "TEXT", nullable: true),
                    VisitCount = table.Column<int>(type: "INTEGER", nullable: true),
                    IsAway = table.Column<bool>(type: "INTEGER", nullable: false),
                    CustomTitle = table.Column<string>(type: "TEXT", nullable: true),
                    CoverFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    CoverIsManual = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    PinnedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSeen = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeenAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    VideoCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Stories_Files_CoverFileId",
                        column: x => x.CoverFileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Stories_Persons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoryItems",
                columns: table => new
                {
                    StoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Day = table.Column<int>(type: "INTEGER", nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryItems", x => new { x.StoryId, x.FileId });
                    table.ForeignKey(
                        name: "FK_StoryItems_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StoryItems_Stories_StoryId",
                        column: x => x.StoryId,
                        principalTable: "Stories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Stories_CoverFile",
                table: "Stories",
                column: "CoverFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Stories_Group_Kind_Start",
                table: "Stories",
                columns: new[] { "GroupId", "Kind", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Stories_Person",
                table: "Stories",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "UX_Stories_Group_Signature",
                table: "Stories",
                columns: new[] { "GroupId", "Signature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoryItems_File",
                table: "StoryItems",
                column: "FileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoryItems");

            migrationBuilder.DropTable(
                name: "Stories");
        }
    }
}
