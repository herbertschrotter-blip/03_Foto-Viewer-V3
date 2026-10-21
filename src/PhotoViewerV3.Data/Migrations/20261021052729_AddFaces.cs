using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoViewerV3.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FaceScans",
                columns: table => new
                {
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ScannedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    FaceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceScans", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_FaceScans_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Persons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CoverFaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Persons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Faces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    BoxX = table.Column<float>(type: "REAL", nullable: false),
                    BoxY = table.Column<float>(type: "REAL", nullable: false),
                    BoxW = table.Column<float>(type: "REAL", nullable: false),
                    BoxH = table.Column<float>(type: "REAL", nullable: false),
                    Landmarks = table.Column<byte[]>(type: "BLOB", nullable: false),
                    DetScore = table.Column<float>(type: "REAL", nullable: false),
                    Size = table.Column<float>(type: "REAL", nullable: false),
                    Sharpness = table.Column<float>(type: "REAL", nullable: false),
                    Quality = table.Column<float>(type: "REAL", nullable: false),
                    Embedding = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ModelVersion = table.Column<string>(type: "TEXT", nullable: false),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    SuggestedPersonId = table.Column<int>(type: "INTEGER", nullable: true),
                    SuggestionScore = table.Column<float>(type: "REAL", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Faces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Faces_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Faces_Persons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Faces_Persons_SuggestedPersonId",
                        column: x => x.SuggestedPersonId,
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "FaceRejections",
                columns: table => new
                {
                    FaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceRejections", x => new { x.FaceId, x.PersonId });
                    table.ForeignKey(
                        name: "FK_FaceRejections_Faces_FaceId",
                        column: x => x.FaceId,
                        principalTable: "Faces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FaceRejections_Persons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FaceRejections_Person",
                table: "FaceRejections",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Faces_File",
                table: "Faces",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_Faces_Person",
                table: "Faces",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Faces_State",
                table: "Faces",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Faces_SuggestedPerson",
                table: "Faces",
                column: "SuggestedPersonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FaceRejections");

            migrationBuilder.DropTable(
                name: "FaceScans");

            migrationBuilder.DropTable(
                name: "Faces");

            migrationBuilder.DropTable(
                name: "Persons");
        }
    }
}
