using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PhotoViewerV3.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCategorySortIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FileCategoryValues_Category",
                table: "FileCategoryValues");

            migrationBuilder.CreateIndex(
                name: "IX_FileCategoryValues_Category_Value_File",
                table: "FileCategoryValues",
                columns: new[] { "CategoryId", "ValueId", "FileId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FileCategoryValues_Category_Value_File",
                table: "FileCategoryValues");

            migrationBuilder.CreateIndex(
                name: "IX_FileCategoryValues_Category",
                table: "FileCategoryValues",
                column: "CategoryId");
        }
    }
}
