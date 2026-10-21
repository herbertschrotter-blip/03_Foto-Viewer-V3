using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoViewerV3.Data.Migrations
{
    /// <summary>
    /// Alben und Ordner-Verknüpfungen (ADR-0054): nur neue Tabellen (<c>Albums</c> je Bibliotheks-Gruppe, <c>AlbumItems</c>,
    /// <c>FolderLinks</c>, <c>FileLinkUndo</c>) — kein Neuaufbau bestehender Tabellen, keine Datenübernahme.
    /// </summary>
    public partial class AddAlbums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Albums",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    FilterJson = table.Column<string>(type: "TEXT", nullable: true),
                    CoverFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Albums", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Albums_Files_CoverFileId",
                        column: x => x.CoverFileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "FileLinkUndo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileOperationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    FileModifiedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileLinkUndo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileLinkUndo_FileOperations_FileOperationId",
                        column: x => x.FileOperationId,
                        principalTable: "FileOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FolderLinks",
                columns: table => new
                {
                    FolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderLinks", x => new { x.FolderId, x.FileId });
                    table.ForeignKey(
                        name: "FK_FolderLinks_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FolderLinks_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlbumItems",
                columns: table => new
                {
                    AlbumId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumItems", x => new { x.AlbumId, x.FileId });
                    table.ForeignKey(
                        name: "FK_AlbumItems_Albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "Albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlbumItems_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlbumItems_File",
                table: "AlbumItems",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_CoverFile",
                table: "Albums",
                column: "CoverFileId");

            migrationBuilder.CreateIndex(
                name: "UX_Albums_Group_Name",
                table: "Albums",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileLinkUndo_Operation",
                table: "FileLinkUndo",
                column: "FileOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderLinks_File",
                table: "FolderLinks",
                column: "FileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumItems");

            migrationBuilder.DropTable(
                name: "FileLinkUndo");

            migrationBuilder.DropTable(
                name: "FolderLinks");

            migrationBuilder.DropTable(
                name: "Albums");
        }
    }
}
