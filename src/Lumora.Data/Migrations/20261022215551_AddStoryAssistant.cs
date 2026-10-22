using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Story-Assistent (ADR-0063): nur neue Tabellen (<c>AssistantHistory</c> „Zuletzt gefragt“, <c>AssistantAliases</c>
    /// gemerkte Antworten auf Rückfragen) — kein Neuaufbau bestehender Tabellen, keine Datenübernahme.
    /// </summary>
    public partial class AddStoryAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantAliases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Term = table.Column<string>(type: "TEXT", nullable: false),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantAliases_Persons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Sentence = table.Column<string>(type: "TEXT", nullable: false),
                    IntentJson = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAliases_Person",
                table: "AssistantAliases",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "UX_AssistantAliases_Group_Term_Person",
                table: "AssistantAliases",
                columns: new[] { "GroupId", "Term", "PersonId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantHistory_Group_Used",
                table: "AssistantHistory",
                columns: new[] { "GroupId", "LastUsedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAliases");

            migrationBuilder.DropTable(
                name: "AssistantHistory");
        }
    }
}
