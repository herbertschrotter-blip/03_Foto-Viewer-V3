using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Bibliotheks-Trennung (ADR-0048): Tabelle <c>LibraryGroups</c>, Spalte <c>GroupId</c> an Libraries, Tags, Categories,
    /// Persons und PersonRelations (je OHNE Fremdschlüssel — <c>ALTER TABLE … ADD COLUMN</c>, kein Neuaufbau einer Tabelle,
    /// der per Kaskade Zuordnungen löschen könnte, vgl. ADR-0045), Eindeutigkeiten je Gruppe. Dazwischen die verlustfreie
    /// Datenübernahme <see cref="AssignGroupsSql"/>.
    /// </summary>
    public partial class AddLibraryScoping : Migration
    {
        /// <summary>
        /// Datenübernahme (für Tests öffentlich). Jede Bibliothek bekommt eine eigene Gruppe (Gruppen-Id = Bibliotheks-Id).
        /// Ein Begriff gehört danach der Gruppe jeder Bibliothek, die ihn benutzt: die kleinste behält die Zeile, jede
        /// weitere bekommt eine Kopie mit expliziter neuer Id, und die Zuordnungen dieser Bibliothek werden auf die Kopie
        /// umgehängt. Ungenutzte Begriffe bekommt JEDE Bibliothek (Herbert findet seine Begriffe überall wieder; mit einer
        /// Bibliothek ändert sich nichts). Ohne Bibliothek bleibt GroupId = 0 — die erste Bibliothek übernimmt sie später.
        /// Benutzt = Tags: FileTags; Kategorien: FileCategoryValues (Kopien bekommen ALLE Werte); Personen: bestätigte
        /// Gesichter, Vorschläge und Ablehnungen; Beziehungen: Personen der Gruppe. Titelbilder bleiben nur dort, wo das
        /// Gesicht liegt (sonst NULL — die Leser nehmen dann das beste Gesicht).
        /// </summary>
        public const string AssignGroupsSql = """
            INSERT INTO LibraryGroups (Id, Name, CreatedAtUtc) SELECT Id, Name, CreatedAt FROM Libraries;
            UPDATE Libraries SET GroupId = Id;

            CREATE TEMP TABLE _ScopeTargets (Kind TEXT NOT NULL, OldId INTEGER NOT NULL, GroupId INTEGER NOT NULL,
                                             PRIMARY KEY (Kind, OldId, GroupId));
            CREATE TEMP TABLE _ScopeMap (Kind TEXT NOT NULL, OldId INTEGER NOT NULL, GroupId INTEGER NOT NULL, NewId INTEGER NOT NULL,
                                         PRIMARY KEY (Kind, OldId, GroupId));

            -- ---- Tags -------------------------------------------------------------------------------------------
            INSERT OR IGNORE INTO _ScopeTargets SELECT 'T', ft.TagId, f.LibraryId FROM FileTags ft JOIN Files f ON f.Id = ft.FileId;
            INSERT OR IGNORE INTO _ScopeTargets SELECT 'T', t.Id, l.Id FROM Tags t CROSS JOIN Libraries l
                WHERE NOT EXISTS (SELECT 1 FROM _ScopeTargets x WHERE x.Kind = 'T' AND x.OldId = t.Id);
            UPDATE Tags SET GroupId = COALESCE((SELECT MIN(x.GroupId) FROM _ScopeTargets x WHERE x.Kind = 'T' AND x.OldId = Tags.Id), 0);
            INSERT INTO _ScopeMap SELECT 'T', Id, GroupId, Id FROM Tags WHERE GroupId <> 0;
            INSERT INTO _ScopeMap
                SELECT 'T', x.OldId, x.GroupId, (SELECT COALESCE(MAX(Id), 0) FROM Tags) + ROW_NUMBER() OVER (ORDER BY x.OldId, x.GroupId)
                FROM _ScopeTargets x JOIN Tags t ON t.Id = x.OldId WHERE x.Kind = 'T' AND x.GroupId <> t.GroupId;
            INSERT INTO Tags (Id, Name, Color, SortOrder, CreatedAtUtc, GroupId)
                SELECT m.NewId, t.Name, t.Color, t.SortOrder, t.CreatedAtUtc, m.GroupId
                FROM _ScopeMap m JOIN Tags t ON t.Id = m.OldId WHERE m.Kind = 'T' AND m.NewId <> m.OldId;
            UPDATE FileTags SET TagId = (SELECT m.NewId FROM _ScopeMap m JOIN Files f ON f.Id = FileTags.FileId
                                         WHERE m.Kind = 'T' AND m.OldId = FileTags.TagId AND m.GroupId = f.LibraryId)
            WHERE EXISTS (SELECT 1 FROM _ScopeMap m JOIN Files f ON f.Id = FileTags.FileId
                          WHERE m.Kind = 'T' AND m.OldId = FileTags.TagId AND m.GroupId = f.LibraryId AND m.NewId <> m.OldId);

            -- ---- Kategorien und Werte ---------------------------------------------------------------------------
            INSERT OR IGNORE INTO _ScopeTargets SELECT 'C', cv.CategoryId, f.LibraryId FROM FileCategoryValues cv JOIN Files f ON f.Id = cv.FileId;
            INSERT OR IGNORE INTO _ScopeTargets SELECT 'C', c.Id, l.Id FROM Categories c CROSS JOIN Libraries l
                WHERE NOT EXISTS (SELECT 1 FROM _ScopeTargets x WHERE x.Kind = 'C' AND x.OldId = c.Id);
            UPDATE Categories SET GroupId = COALESCE((SELECT MIN(x.GroupId) FROM _ScopeTargets x WHERE x.Kind = 'C' AND x.OldId = Categories.Id), 0);
            INSERT INTO _ScopeMap SELECT 'C', Id, GroupId, Id FROM Categories WHERE GroupId <> 0;
            INSERT INTO _ScopeMap
                SELECT 'C', x.OldId, x.GroupId, (SELECT COALESCE(MAX(Id), 0) FROM Categories) + ROW_NUMBER() OVER (ORDER BY x.OldId, x.GroupId)
                FROM _ScopeTargets x JOIN Categories c ON c.Id = x.OldId WHERE x.Kind = 'C' AND x.GroupId <> c.GroupId;
            INSERT INTO Categories (Id, Name, SortOrder, CreatedAtUtc, GroupId)
                SELECT m.NewId, c.Name, c.SortOrder, c.CreatedAtUtc, m.GroupId
                FROM _ScopeMap m JOIN Categories c ON c.Id = m.OldId WHERE m.Kind = 'C' AND m.NewId <> m.OldId;
            INSERT INTO _ScopeMap
                SELECT 'V', v.Id, m.GroupId, v.Id FROM CategoryValues v
                JOIN _ScopeMap m ON m.Kind = 'C' AND m.OldId = v.CategoryId AND m.NewId = m.OldId;
            INSERT INTO _ScopeMap
                SELECT 'V', v.Id, m.GroupId, (SELECT COALESCE(MAX(Id), 0) FROM CategoryValues) + ROW_NUMBER() OVER (ORDER BY m.GroupId, v.Id)
                FROM _ScopeMap m JOIN CategoryValues v ON v.CategoryId = m.OldId WHERE m.Kind = 'C' AND m.NewId <> m.OldId;
            INSERT INTO CategoryValues (Id, CategoryId, Name, SortOrder)
                SELECT vm.NewId, cm.NewId, v.Name, v.SortOrder
                FROM _ScopeMap vm JOIN CategoryValues v ON v.Id = vm.OldId
                JOIN _ScopeMap cm ON cm.Kind = 'C' AND cm.OldId = v.CategoryId AND cm.GroupId = vm.GroupId
                WHERE vm.Kind = 'V' AND vm.NewId <> vm.OldId;
            UPDATE FileCategoryValues SET
                CategoryId = (SELECT m.NewId FROM _ScopeMap m JOIN Files f ON f.Id = FileCategoryValues.FileId
                              WHERE m.Kind = 'C' AND m.OldId = FileCategoryValues.CategoryId AND m.GroupId = f.LibraryId),
                ValueId = (SELECT m.NewId FROM _ScopeMap m JOIN Files f ON f.Id = FileCategoryValues.FileId
                           WHERE m.Kind = 'V' AND m.OldId = FileCategoryValues.ValueId AND m.GroupId = f.LibraryId)
            WHERE EXISTS (SELECT 1 FROM _ScopeMap m JOIN Files f ON f.Id = FileCategoryValues.FileId
                          WHERE m.Kind = 'C' AND m.OldId = FileCategoryValues.CategoryId AND m.GroupId = f.LibraryId AND m.NewId <> m.OldId);

            -- ---- Personen ---------------------------------------------------------------------------------------
            INSERT OR IGNORE INTO _ScopeTargets SELECT 'P', x.PersonId, f.LibraryId FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE x.PersonId IS NOT NULL;
            INSERT OR IGNORE INTO _ScopeTargets SELECT 'P', x.SuggestedPersonId, f.LibraryId FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE x.SuggestedPersonId IS NOT NULL;
            INSERT OR IGNORE INTO _ScopeTargets SELECT 'P', r.PersonId, f.LibraryId FROM FaceRejections r JOIN Faces x ON x.Id = r.FaceId JOIN Files f ON f.Id = x.FileId;
            INSERT OR IGNORE INTO _ScopeTargets SELECT 'P', p.Id, l.Id FROM Persons p CROSS JOIN Libraries l
                WHERE NOT EXISTS (SELECT 1 FROM _ScopeTargets x WHERE x.Kind = 'P' AND x.OldId = p.Id);
            UPDATE Persons SET GroupId = COALESCE((SELECT MIN(x.GroupId) FROM _ScopeTargets x WHERE x.Kind = 'P' AND x.OldId = Persons.Id), 0);
            INSERT INTO _ScopeMap SELECT 'P', Id, GroupId, Id FROM Persons WHERE GroupId <> 0;
            INSERT INTO _ScopeMap
                SELECT 'P', x.OldId, x.GroupId, (SELECT COALESCE(MAX(Id), 0) FROM Persons) + ROW_NUMBER() OVER (ORDER BY x.OldId, x.GroupId)
                FROM _ScopeTargets x JOIN Persons p ON p.Id = x.OldId WHERE x.Kind = 'P' AND x.GroupId <> p.GroupId;
            INSERT INTO Persons (Id, Name, CreatedAtUtc, CoverFaceId, IsHidden, FirstName, LastName, Nickname, RelationId,
                                 BirthYear, BirthMonth, BirthDay, IsMe, GroupId)
                SELECT m.NewId, p.Name, p.CreatedAtUtc,
                       CASE WHEN EXISTS (SELECT 1 FROM Faces x JOIN Files f ON f.Id = x.FileId
                                         WHERE x.Id = p.CoverFaceId AND f.LibraryId = m.GroupId) THEN p.CoverFaceId END,
                       p.IsHidden, p.FirstName, p.LastName, p.Nickname, p.RelationId, p.BirthYear, p.BirthMonth, p.BirthDay, p.IsMe,
                       m.GroupId
                FROM _ScopeMap m JOIN Persons p ON p.Id = m.OldId WHERE m.Kind = 'P' AND m.NewId <> m.OldId;
            UPDATE Persons SET CoverFaceId = NULL
            WHERE CoverFaceId IS NOT NULL AND GroupId <> 0
              AND NOT EXISTS (SELECT 1 FROM Faces x JOIN Files f ON f.Id = x.FileId
                              WHERE x.Id = Persons.CoverFaceId AND f.LibraryId = Persons.GroupId);
            UPDATE Faces SET PersonId = (SELECT m.NewId FROM _ScopeMap m JOIN Files f ON f.Id = Faces.FileId
                                         WHERE m.Kind = 'P' AND m.OldId = Faces.PersonId AND m.GroupId = f.LibraryId)
            WHERE PersonId IS NOT NULL
              AND EXISTS (SELECT 1 FROM _ScopeMap m JOIN Files f ON f.Id = Faces.FileId
                          WHERE m.Kind = 'P' AND m.OldId = Faces.PersonId AND m.GroupId = f.LibraryId AND m.NewId <> m.OldId);
            UPDATE Faces SET SuggestedPersonId = (SELECT m.NewId FROM _ScopeMap m JOIN Files f ON f.Id = Faces.FileId
                                                  WHERE m.Kind = 'P' AND m.OldId = Faces.SuggestedPersonId AND m.GroupId = f.LibraryId)
            WHERE SuggestedPersonId IS NOT NULL
              AND EXISTS (SELECT 1 FROM _ScopeMap m JOIN Files f ON f.Id = Faces.FileId
                          WHERE m.Kind = 'P' AND m.OldId = Faces.SuggestedPersonId AND m.GroupId = f.LibraryId AND m.NewId <> m.OldId);
            UPDATE FaceRejections SET PersonId = (SELECT m.NewId FROM _ScopeMap m JOIN Faces x ON x.Id = FaceRejections.FaceId
                                                  JOIN Files f ON f.Id = x.FileId
                                                  WHERE m.Kind = 'P' AND m.OldId = FaceRejections.PersonId AND m.GroupId = f.LibraryId)
            WHERE EXISTS (SELECT 1 FROM _ScopeMap m JOIN Faces x ON x.Id = FaceRejections.FaceId JOIN Files f ON f.Id = x.FileId
                          WHERE m.Kind = 'P' AND m.OldId = FaceRejections.PersonId AND m.GroupId = f.LibraryId AND m.NewId <> m.OldId);

            -- ---- Beziehungen (nach den Personen: benutzt = Personen der Gruppe) -------------------------------------
            INSERT OR IGNORE INTO _ScopeTargets SELECT 'R', p.RelationId, p.GroupId FROM Persons p WHERE p.RelationId IS NOT NULL AND p.GroupId <> 0;
            INSERT OR IGNORE INTO _ScopeTargets SELECT 'R', r.Id, l.Id FROM PersonRelations r CROSS JOIN Libraries l
                WHERE NOT EXISTS (SELECT 1 FROM _ScopeTargets x WHERE x.Kind = 'R' AND x.OldId = r.Id);
            UPDATE PersonRelations SET GroupId = COALESCE((SELECT MIN(x.GroupId) FROM _ScopeTargets x WHERE x.Kind = 'R' AND x.OldId = PersonRelations.Id), 0);
            INSERT INTO _ScopeMap SELECT 'R', Id, GroupId, Id FROM PersonRelations WHERE GroupId <> 0;
            INSERT INTO _ScopeMap
                SELECT 'R', x.OldId, x.GroupId, (SELECT COALESCE(MAX(Id), 0) FROM PersonRelations) + ROW_NUMBER() OVER (ORDER BY x.OldId, x.GroupId)
                FROM _ScopeTargets x JOIN PersonRelations r ON r.Id = x.OldId WHERE x.Kind = 'R' AND x.GroupId <> r.GroupId;
            INSERT INTO PersonRelations (Id, Name, GroupId)
                SELECT m.NewId, r.Name, m.GroupId FROM _ScopeMap m JOIN PersonRelations r ON r.Id = m.OldId
                WHERE m.Kind = 'R' AND m.NewId <> m.OldId;
            UPDATE Persons SET RelationId = (SELECT m.NewId FROM _ScopeMap m
                                             WHERE m.Kind = 'R' AND m.OldId = Persons.RelationId AND m.GroupId = Persons.GroupId)
            WHERE RelationId IS NOT NULL
              AND EXISTS (SELECT 1 FROM _ScopeMap m
                          WHERE m.Kind = 'R' AND m.OldId = Persons.RelationId AND m.GroupId = Persons.GroupId AND m.NewId <> m.OldId);

            DROP TABLE _ScopeTargets;
            DROP TABLE _ScopeMap;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Erst die globalen Eindeutigkeiten weg (Kopien tragen denselben Namen in einer anderen Gruppe).
            migrationBuilder.DropIndex(
                name: "UX_Tags_Name",
                table: "Tags");

            migrationBuilder.DropIndex(
                name: "IX_Persons_IsMe",
                table: "Persons");

            migrationBuilder.DropIndex(
                name: "IX_PersonRelations_Name",
                table: "PersonRelations");

            migrationBuilder.DropIndex(
                name: "UX_Categories_Name",
                table: "Categories");

            // Spalten per ALTER TABLE … ADD COLUMN (NOT NULL DEFAULT 0, ohne Fremdschlüssel) — kein Neuaufbau einer Tabelle.
            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Tags",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Persons",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "PersonRelations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Categories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LibraryGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryGroups", x => x.Id);
                });

            migrationBuilder.Sql(AssignGroupsSql);

            migrationBuilder.CreateIndex(
                name: "UX_Tags_Group_Name",
                table: "Tags",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Persons_Group_IsMe",
                table: "Persons",
                columns: new[] { "GroupId", "IsMe" },
                unique: true,
                filter: "\"IsMe\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Persons_Group_Name",
                table: "Persons",
                columns: new[] { "GroupId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonRelations_Group_Name",
                table: "PersonRelations",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Libraries_Group",
                table: "Libraries",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "UX_Categories_Group_Name",
                table: "Categories",
                columns: new[] { "GroupId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Bewusst kein Rückweg: DropColumn ließe EF Tags/Categories/Persons/PersonRelations neu aufbauen (DROP TABLE mit
            // eingeschalteten Fremdschlüsseln → Zuordnungen weg), und Kopien je Bibliothek lassen sich nicht eindeutig
            // zurückführen. Vor jeder Migration legt der Start eine Sicherung an (PLAN Kap. 14) — die ist der Weg zurück.
            throw new NotSupportedException("AddLibraryScoping lässt sich nicht zurücknehmen — die Sicherung vor der Migration verwenden.");
        }
    }
}
