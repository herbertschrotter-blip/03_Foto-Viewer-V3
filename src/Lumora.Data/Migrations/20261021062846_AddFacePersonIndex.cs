using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFacePersonIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Faces_File_State_Person",
                table: "Faces",
                columns: new[] { "FileId", "State", "PersonId", "SuggestedPersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_Faces_Person_State_File",
                table: "Faces",
                columns: new[] { "PersonId", "State", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_Faces_SuggestedPerson_State_File",
                table: "Faces",
                columns: new[] { "SuggestedPersonId", "State", "FileId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Faces_File_State_Person",
                table: "Faces");

            migrationBuilder.DropIndex(
                name: "IX_Faces_Person_State_File",
                table: "Faces");

            migrationBuilder.DropIndex(
                name: "IX_Faces_SuggestedPerson_State_File",
                table: "Faces");
        }
    }
}
