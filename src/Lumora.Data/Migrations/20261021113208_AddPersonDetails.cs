using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonDetails : Migration
    {
        /// <summary>Teilt Persons.Name am ersten Leerzeichen in FirstName/LastName (für Tests öffentlich).</summary>
        public const string SplitNamesSql = """
            UPDATE "Persons" SET
                "FirstName" = CASE WHEN instr(trim("Name"), ' ') > 0
                                   THEN substr(trim("Name"), 1, instr(trim("Name"), ' ') - 1)
                                   ELSE trim("Name") END,
                "LastName" = CASE WHEN instr(trim("Name"), ' ') > 0
                                  THEN substr(trim("Name"), instr(trim("Name"), ' ') + 1)
                                  ELSE '' END;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BirthDay",
                table: "Persons",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BirthMonth",
                table: "Persons",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BirthYear",
                table: "Persons",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "Persons",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                collation: "NOCASE");

            migrationBuilder.AddColumn<bool>(
                name: "IsMe",
                table: "Persons",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                table: "Persons",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                collation: "NOCASE");

            migrationBuilder.AddColumn<string>(
                name: "Nickname",
                table: "Persons",
                type: "TEXT",
                nullable: true,
                collation: "NOCASE");

            migrationBuilder.CreateTable(
                name: "PersonRelations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonRelations", x => x.Id);
                });

            // Fremdschlüssel direkt in ADD COLUMN statt AddForeignKey: AddForeignKey ließe EF die Tabelle Persons neu
            // aufbauen (DROP TABLE Persons) — mit eingeschalteten Fremdschlüsseln würde das die Zuordnungen in Faces auf
            // NULL setzen und die Ablehnungen löschen. SQLite erlaubt REFERENCES bei ADD COLUMN mit Vorgabe NULL.
            migrationBuilder.Sql(
                "ALTER TABLE \"Persons\" ADD COLUMN \"RelationId\" INTEGER NULL " +
                "REFERENCES \"PersonRelations\" (\"Id\") ON DELETE SET NULL;");

            // Bestehende Personen: Name am ERSTEN Leerzeichen teilen (erstes Wort = Vorname, Rest = Nachname, ein Wort =
            // nur Vorname). Verlustfrei: Name bleibt unverändert (Anzeigename), Vorname + ' ' + Nachname = Name.
            migrationBuilder.Sql(SplitNamesSql);

            migrationBuilder.CreateIndex(
                name: "IX_Persons_IsMe",
                table: "Persons",
                column: "IsMe",
                unique: true,
                filter: "\"IsMe\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Persons_Relation",
                table: "Persons",
                column: "RelationId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonRelations_Name",
                table: "PersonRelations",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Bewusst kein Rückweg: DropForeignKey/DropColumn ließe EF die Tabelle Persons neu aufbauen (DROP TABLE mit
            // eingeschalteten Fremdschlüsseln → Zuordnungen in Faces NULL, Ablehnungen gelöscht). Vor jeder Migration legt
            // der Start eine Sicherung an (PLAN Kap. 14) — die ist der Weg zurück.
            throw new NotSupportedException("AddPersonDetails lässt sich nicht zurücknehmen — die Sicherung vor der Migration verwenden.");
        }
    }
}
