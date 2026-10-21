using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumora.Data.Migrations
{
    /// <summary>
    /// Zusammenbau 0.2.56 (ADR-0048 Nachtrag, ADR-0052): Motiv-Begriffe gehören wie Tags einer Bibliotheks-Gruppe. Spalte
    /// <c>MotifTerms.GroupId</c> per <c>ALTER TABLE … ADD COLUMN</c> (kein Neuaufbau — FileMotifs hängen per Kaskade an
    /// MotifTerms), befüllt aus <c>Libraries.GroupId</c>; gleichnamige Begriffe einer Gruppe werden eins
    /// (<see cref="GroupMotifTermsSql"/>), Eindeutigkeit je (GroupId, Name). <c>LibraryId</c> bleibt als Anker des
    /// Fremdschlüssels (eigener Index für die Kaskade) und wird für den Bereich nicht mehr gelesen.
    /// </summary>
    public partial class MotifTermsByGroup : Migration
    {
        /// <summary>
        /// Datenübernahme (für Tests öffentlich): GroupId = Gruppe der Anker-Bibliothek. Gleichnamige Begriffe einer Gruppe
        /// (nur bei schon verknüpften Bibliotheken möglich) → der mit der kleinsten Id bleibt; Treffer der übrigen wandern
        /// zu ihm (höhere Wahrscheinlichkeit gewinnt), dann werden sie gelöscht. In Gruppen mit mehreren Bibliotheken
        /// verfallen die Text-Vektoren: jeder Begriff wurde bisher nur gegen die Fotos seiner eigenen Bibliothek
        /// verglichen — der Motiv-Dienst rechnet Vektor und Treffer aus den gespeicherten Bild-Vektoren neu (kein Foto wird
        /// neu analysiert). Merker der Start-Liste je Gruppe statt je Bibliothek (<c>motifs.seeded.g{GroupId}</c>).
        /// </summary>
        public const string GroupMotifTermsSql = """
            UPDATE MotifTerms SET GroupId = COALESCE((SELECT l.GroupId FROM Libraries l WHERE l.Id = MotifTerms.LibraryId), 0);

            CREATE TEMP TABLE _MotifMerge (SourceId INTEGER NOT NULL PRIMARY KEY, TargetId INTEGER NOT NULL);
            INSERT INTO _MotifMerge
                SELECT s.Id, (SELECT MIN(t.Id) FROM MotifTerms t WHERE t.GroupId = s.GroupId AND t.Name = s.Name)
                FROM MotifTerms s
                WHERE s.GroupId <> 0
                  AND s.Id <> (SELECT MIN(t.Id) FROM MotifTerms t WHERE t.GroupId = s.GroupId AND t.Name = s.Name);
            INSERT INTO FileMotifs (FileId, TermId, Score)
                SELECT fm.FileId, m.TargetId, fm.Score FROM FileMotifs fm JOIN _MotifMerge m ON m.SourceId = fm.TermId WHERE 1
                ON CONFLICT(FileId, TermId) DO UPDATE SET Score = MAX(Score, excluded.Score);
            DELETE FROM FileMotifs WHERE TermId IN (SELECT SourceId FROM _MotifMerge);
            DELETE FROM MotifTerms WHERE Id IN (SELECT SourceId FROM _MotifMerge);
            DROP TABLE _MotifMerge;

            UPDATE MotifTerms SET TextEmbedding = NULL, TextModelVersion = NULL
            WHERE GroupId IN (SELECT GroupId FROM Libraries WHERE GroupId <> 0 GROUP BY GroupId HAVING COUNT(*) > 1);

            INSERT OR IGNORE INTO Settings (Key, Value)
                SELECT 'motifs.seeded.g' || l.GroupId, 'true' FROM Settings s
                JOIN Libraries l ON s.Key = 'motifs.seeded.' || l.Id
                WHERE l.GroupId <> 0;
            DELETE FROM Settings WHERE Key LIKE 'motifs.seeded.%' AND Key NOT LIKE 'motifs.seeded.g%';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "MotifTerms",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.DropIndex(
                name: "UX_MotifTerms_Library_Name",
                table: "MotifTerms");

            migrationBuilder.Sql(GroupMotifTermsSql);

            migrationBuilder.CreateIndex(
                name: "IX_MotifTerms_Library",
                table: "MotifTerms",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "UX_MotifTerms_Group_Name",
                table: "MotifTerms",
                columns: new[] { "GroupId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Bewusst kein Rückweg (wie AddLibraryScoping): DropColumn ließe EF MotifTerms neu aufbauen (DROP TABLE mit
            // eingeschalteten Fremdschlüsseln → alle Treffer in FileMotifs weg), und zusammengeführte Begriffe lassen sich
            // nicht wieder je Bibliothek trennen. Der Weg zurück ist die Sicherung vor der Migration (PLAN Kap. 14).
            throw new NotSupportedException("MotifTermsByGroup lässt sich nicht zurücknehmen — die Sicherung vor der Migration verwenden.");
        }
    }
}
