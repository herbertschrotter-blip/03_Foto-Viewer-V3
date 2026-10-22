using Microsoft.Data.Sqlite;
using static Lumora.Services.Library.LibraryGroupStore;

namespace Lumora.Services.Library;

/// <summary>
/// SQL für Verknüpfen und Lösen (ADR-0048) — alles in der Transaktion des Aufrufers. Begriffe werden über ihren Namen
/// (Spalten mit NOCASE) zusammengeführt; Zuordnungen wandern per <c>INSERT OR IGNORE</c> bzw. <c>UPDATE</c> um, bevor die
/// zusammengeführte Quelle gelöscht wird, damit keine Kaskade etwas mitnimmt.
/// </summary>
internal static class LibraryLinkStore
{
    // ---- Vorschau ---------------------------------------------------------------------------------------------------

    public static LibraryLinkPreview PreviewLink(SqliteConnection connection, int libraryId, int otherLibraryId, int targetGroup, int sourceGroup)
    {
        int Merged(string table) => (int)Scalar(connection, null,
            $"SELECT COUNT(*) FROM {table} s WHERE s.GroupId = $s AND EXISTS (SELECT 1 FROM {table} t WHERE t.GroupId = $t AND t.Name = s.Name);",
            ("$s", sourceGroup), ("$t", targetGroup));
        int All(string table) => (int)Scalar(connection, null, $"SELECT COUNT(*) FROM {table} WHERE GroupId = $s;", ("$s", sourceGroup));

        var tagsMerged = Merged("Tags");
        var categoriesMerged = Merged("Categories");
        var relationsMerged = Merged("PersonRelations");
        var conflicts = ReadPersonConflicts(connection, null, targetGroup, sourceGroup);
        return new LibraryLinkPreview(
            libraryId,
            ReadLibraryName(connection, null, libraryId),
            otherLibraryId,
            ReadLibraryName(connection, null, otherLibraryId),
            tagsMerged,
            All("Tags") - tagsMerged,
            categoriesMerged,
            All("Categories") - categoriesMerged,
            relationsMerged,
            All("PersonRelations") - relationsMerged,
            All("Persons") - conflicts.Count,
            conflicts);
    }

    public static List<LibraryLinkPersonConflict> ReadPersonConflicts(SqliteConnection connection, SqliteTransaction? tx, int targetGroup, int sourceGroup)
    {
        using var cmd = Command(connection, tx, """
            SELECT t.Name, t.Id, s.Id FROM Persons s
            JOIN Persons t ON t.GroupId = $t AND t.Name = s.Name
            WHERE s.GroupId = $s
            ORDER BY t.Name COLLATE NOCASE, s.Id, t.Id;
            """, ("$s", sourceGroup), ("$t", targetGroup));
        var list = new List<LibraryLinkPersonConflict>();
        var seenSources = new HashSet<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (seenSources.Add(reader.GetInt32(2)))
            {
                list.Add(new LibraryLinkPersonConflict(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
            }
        }

        return list;
    }

    public static LibraryUnlinkPreview PreviewUnlink(SqliteConnection connection, int libraryId, int groupId)
    {
        int All(string table) => (int)Scalar(connection, null, $"SELECT COUNT(*) FROM {table} WHERE GroupId = $g;", ("$g", groupId));
        var others = new List<string>();
        using (var cmd = Command(connection, null, "SELECT Name FROM Libraries WHERE GroupId = $g AND Id <> $id ORDER BY Name COLLATE NOCASE;",
                   ("$g", groupId), ("$id", libraryId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                others.Add(reader.GetString(0));
            }
        }

        return new LibraryUnlinkPreview(
            libraryId,
            ReadLibraryName(connection, null, libraryId),
            ReadGroupName(connection, null, groupId),
            others,
            All("Tags"),
            All("Categories"),
            All("PersonRelations"),
            All("Persons"));
    }

    // ---- Verknüpfen -------------------------------------------------------------------------------------------------

    /// <summary>Gruppe <paramref name="sourceGroup"/> geht in <paramref name="targetGroup"/> auf.</summary>
    public static LibraryLinkResult Link(
        SqliteConnection connection,
        SqliteTransaction tx,
        int targetGroup,
        int sourceGroup,
        string sourceLibraryName,
        IReadOnlyCollection<int> keepApartSourcePersonIds)
    {
        var p = new (string, object)[] { ("$t", targetGroup), ("$s", sourceGroup) };
        Execute(connection, tx, "DROP TABLE IF EXISTS temp._LinkMap;");
        Execute(connection, tx, "CREATE TEMP TABLE _LinkMap (Kind TEXT NOT NULL, SourceId INTEGER NOT NULL, TargetId INTEGER NOT NULL, PRIMARY KEY (Kind, SourceId));");

        // ---- Tags: gleichnamige zusammenführen, übrige wandern (Reihenfolge hinter die der Zielgruppe).
        var tagsMerged = Execute(connection, tx,
            "INSERT INTO _LinkMap SELECT 'T', s.Id, t.Id FROM Tags s JOIN Tags t ON t.GroupId = $t AND t.Name = s.Name WHERE s.GroupId = $s;", p);
        Execute(connection, tx, """
            INSERT OR IGNORE INTO FileTags (FileId, TagId)
            SELECT ft.FileId, m.TargetId FROM FileTags ft JOIN _LinkMap m ON m.Kind = 'T' AND m.SourceId = ft.TagId;
            DELETE FROM FileTags WHERE TagId IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'T');
            DELETE FROM Tags WHERE Id IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'T');
            """);
        var tagOffset = Scalar(connection, tx, "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Tags WHERE GroupId = $t;", p);
        Execute(connection, tx, "UPDATE Tags SET GroupId = $t, SortOrder = SortOrder + $o WHERE GroupId = $s;", ("$t", targetGroup), ("$s", sourceGroup), ("$o", tagOffset));

        // ---- Kategorien: gleichnamige zusammenführen; deren Werte gleichen Namens auch, übrige Werte wandern mit.
        var categoriesMerged = Execute(connection, tx,
            "INSERT INTO _LinkMap SELECT 'C', s.Id, t.Id FROM Categories s JOIN Categories t ON t.GroupId = $t AND t.Name = s.Name WHERE s.GroupId = $s;", p);
        Execute(connection, tx, """
            INSERT INTO _LinkMap
            SELECT 'V', sv.Id, tv.Id FROM CategoryValues sv
            JOIN _LinkMap m ON m.Kind = 'C' AND m.SourceId = sv.CategoryId
            JOIN CategoryValues tv ON tv.CategoryId = m.TargetId AND tv.Name = sv.Name;
            """);
        foreach (var (source, target) in ReadMap(connection, tx, "C"))
        {
            var valueOffset = Scalar(connection, tx, "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM CategoryValues WHERE CategoryId = $c;", ("$c", target));
            Execute(connection, tx, """
                UPDATE CategoryValues SET CategoryId = $target, SortOrder = SortOrder + $o
                WHERE CategoryId = $source AND Id NOT IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'V');
                """, ("$target", target), ("$source", source), ("$o", valueOffset));
        }

        Execute(connection, tx, """
            UPDATE FileCategoryValues SET
                ValueId = COALESCE((SELECT m.TargetId FROM _LinkMap m WHERE m.Kind = 'V' AND m.SourceId = FileCategoryValues.ValueId), ValueId),
                CategoryId = (SELECT m.TargetId FROM _LinkMap m WHERE m.Kind = 'C' AND m.SourceId = FileCategoryValues.CategoryId)
            WHERE CategoryId IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'C');
            DELETE FROM CategoryValues WHERE Id IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'V');
            DELETE FROM Categories WHERE Id IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'C');
            """);
        var categoryOffset = Scalar(connection, tx, "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Categories WHERE GroupId = $t;", p);
        Execute(connection, tx, "UPDATE Categories SET GroupId = $t, SortOrder = SortOrder + $o WHERE GroupId = $s;", ("$t", targetGroup), ("$s", sourceGroup), ("$o", categoryOffset));

        // ---- Beziehungen: gleichnamige zusammenführen (Personen zeigen auf die bleibende), übrige wandern.
        var relationsMerged = Execute(connection, tx,
            "INSERT INTO _LinkMap SELECT 'R', s.Id, t.Id FROM PersonRelations s JOIN PersonRelations t ON t.GroupId = $t AND t.Name = s.Name WHERE s.GroupId = $s;", p);
        Execute(connection, tx, """
            UPDATE Persons SET RelationId = (SELECT m.TargetId FROM _LinkMap m WHERE m.Kind = 'R' AND m.SourceId = Persons.RelationId)
            WHERE RelationId IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'R');
            DELETE FROM PersonRelations WHERE Id IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'R');
            """);
        Execute(connection, tx, "UPDATE PersonRelations SET GroupId = $t WHERE GroupId = $s;", p);

        // ---- Personen: gleichnamige zusammenführen (außer ausdrücklich getrennt gehaltene), übrige wandern.
        var conflicts = ReadPersonConflicts(connection, tx, targetGroup, sourceGroup);
        var personsMerged = 0;
        var keptApart = 0;
        foreach (var conflict in conflicts)
        {
            if (keepApartSourcePersonIds.Contains(conflict.SourcePersonId))
            {
                RenameKeptApart(connection, tx, conflict.SourcePersonId, conflict.Name, sourceLibraryName, targetGroup, sourceGroup);
                keptApart++;
            }
            else
            {
                MergePerson(connection, tx, conflict.SourcePersonId, conflict.TargetPersonId);
                personsMerged++;
            }
        }

        // „Das bin ich“ gibt es je Gruppe höchstens einmal: die Zielgruppe gewinnt.
        if (Scalar(connection, tx, "SELECT COUNT(*) FROM Persons WHERE GroupId = $t AND IsMe = 1;", p) > 0)
        {
            Execute(connection, tx, "UPDATE Persons SET IsMe = 0 WHERE GroupId = $s AND IsMe = 1;", p);
        }

        Execute(connection, tx, "UPDATE Persons SET GroupId = $t WHERE GroupId = $s;", p);

        // ---- Motiv-Begriffe (0.2.56)
        var motifTermsMerged = LinkMotifTerms(connection, tx, targetGroup, sourceGroup);
        Albums.AlbumGroupStore.Link(connection, tx, targetGroup, sourceGroup, sourceLibraryName); // Alben (ADR-0054)
        Stories.StoryGroupStore.Link(connection, tx, targetGroup, sourceGroup); // Storys (ADR-0058): gleiche Signatur = eine Story
        Places.Tracks.GpsTrackGroupStore.Link(connection, tx, targetGroup, sourceGroup); // GPS-Spuren (ADR-0062): gleiche Prüfsumme = eine Spur

        // ---- Bibliotheken und Gruppe
        Execute(connection, tx, "UPDATE Libraries SET GroupId = $t WHERE GroupId = $s;", p);
        Execute(connection, tx, "DELETE FROM LibraryGroups WHERE Id = $s;", p);
        if (ReadViewAllLinked(connection, tx) == sourceGroup)
        {
            WriteViewAllLinked(connection, tx, targetGroup);
        }

        Execute(connection, tx, "DROP TABLE temp._LinkMap;");
        return new LibraryLinkResult(targetGroup, tagsMerged, categoriesMerged, relationsMerged, personsMerged, keptApart, motifTermsMerged);
    }

    /// <summary>
    /// Motiv-Begriffe beim Verknüpfen (ADR-0048 Nachtrag 0.2.56): gleichnamige werden eins (die der Zielgruppe bleiben,
    /// Treffer wandern, höhere Wahrscheinlichkeit gewinnt), die übrigen wandern hinter die der Zielgruppe. Bisher wurde jeder
    /// Begriff nur gegen die Fotos seiner eigenen Bibliothek verglichen — wo jetzt Fotos hinzukommen, die er nie gesehen
    /// hat (Begriff nur auf einer Seite, oder gleichnamig mit anderer Beschreibung/Schwelle), verfällt sein Text-Vektor: der
    /// Motiv-Dienst rechnet Vektor und Treffer beim nächsten Start des Index aus den gespeicherten Bild-Vektoren neu (kein
    /// Foto wird neu analysiert). Gleiche Begriffe auf beiden Seiten (z. B. die Start-Liste) bleiben unverändert.
    /// Merker der Start-Liste: hatte eine Seite ihn, hat ihn die Zielgruppe.
    /// </summary>
    private static int LinkMotifTerms(SqliteConnection connection, SqliteTransaction tx, int targetGroup, int sourceGroup)
    {
        var p = new (string, object)[] { ("$t", targetGroup), ("$s", sourceGroup) };
        var merged = Execute(connection, tx,
            "INSERT INTO _LinkMap SELECT 'M', s.Id, t.Id FROM MotifTerms s JOIN MotifTerms t ON t.GroupId = $t AND t.Name = s.Name WHERE s.GroupId = $s;", p);
        Execute(connection, tx, """
            UPDATE MotifTerms SET TextEmbedding = NULL, TextModelVersion = NULL
            WHERE (GroupId = $t AND Id NOT IN (SELECT TargetId FROM _LinkMap WHERE Kind = 'M'))
               OR (GroupId = $s AND Id NOT IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'M'))
               OR Id IN (SELECT m.TargetId FROM _LinkMap m
                         JOIN MotifTerms src ON src.Id = m.SourceId JOIN MotifTerms dst ON dst.Id = m.TargetId
                         WHERE m.Kind = 'M'
                           AND (COALESCE(src.Prompt, '') <> COALESCE(dst.Prompt, '') OR COALESCE(src.Threshold, -1) <> COALESCE(dst.Threshold, -1)
                                OR COALESCE(src.TextModelVersion, '') <> COALESCE(dst.TextModelVersion, '')));
            INSERT INTO FileMotifs (FileId, TermId, Score)
                SELECT fm.FileId, m.TargetId, fm.Score FROM FileMotifs fm JOIN _LinkMap m ON m.Kind = 'M' AND m.SourceId = fm.TermId WHERE 1
                ON CONFLICT(FileId, TermId) DO UPDATE SET Score = MAX(Score, excluded.Score);
            DELETE FROM FileMotifs WHERE TermId IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'M');
            DELETE FROM MotifTerms WHERE Id IN (SELECT SourceId FROM _LinkMap WHERE Kind = 'M');
            """, p);
        var offset = Scalar(connection, tx, "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM MotifTerms WHERE GroupId = $t;", p);
        Execute(connection, tx, "UPDATE MotifTerms SET GroupId = $t, SortOrder = SortOrder + $o WHERE GroupId = $s;",
            ("$t", targetGroup), ("$s", sourceGroup), ("$o", offset));

        if (Scalar(connection, tx, "SELECT COUNT(*) FROM Settings WHERE Key = $k;", ("$k", MotifSeededKey(sourceGroup))) > 0)
        {
            Execute(connection, tx, "INSERT OR IGNORE INTO Settings (Key, Value) VALUES ($k, 'true');", ("$k", MotifSeededKey(targetGroup)));
        }

        Execute(connection, tx, "DELETE FROM Settings WHERE Key = $k;", ("$k", MotifSeededKey(sourceGroup)));
        return merged;
    }

    /// <summary>
    /// Führt eine Person in eine andere zusammen (wie <c>MergePersonsAsync</c>, ADR-0045): Gesichter, Vorschläge und
    /// Ablehnungen wandern; die bleibende behält ihre Namen und bekommt fehlende Beziehung, fehlendes Geburtsdatum und
    /// fehlendes Titelbild; „Das bin ich“ wandert mit, wenn ihre Gruppe noch niemanden so markiert hat.
    /// </summary>
    private static void MergePerson(SqliteConnection connection, SqliteTransaction tx, int sourceId, int targetId)
    {
        var p = new (string, object)[] { ("$s", sourceId), ("$t", targetId) };
        Execute(connection, tx, """
            UPDATE Faces SET PersonId = $t WHERE PersonId = $s;
            UPDATE Faces SET SuggestedPersonId = $t WHERE SuggestedPersonId = $s;
            INSERT OR IGNORE INTO FaceRejections (FaceId, PersonId) SELECT FaceId, $t FROM FaceRejections WHERE PersonId = $s;
            DELETE FROM FaceRejections WHERE PersonId = $s;
            UPDATE Persons SET
                CoverFaceId = COALESCE(CoverFaceId, (SELECT CoverFaceId FROM Persons WHERE Id = $s)),
                RelationId = COALESCE(RelationId, (SELECT RelationId FROM Persons WHERE Id = $s)),
                BirthYear = CASE WHEN BirthYear IS NULL THEN (SELECT BirthYear FROM Persons WHERE Id = $s) ELSE BirthYear END,
                BirthMonth = CASE WHEN BirthYear IS NULL THEN (SELECT BirthMonth FROM Persons WHERE Id = $s) ELSE BirthMonth END,
                BirthDay = CASE WHEN BirthYear IS NULL THEN (SELECT BirthDay FROM Persons WHERE Id = $s) ELSE BirthDay END
            WHERE Id = $t;
            """, p);
        var sourceIsMe = Scalar(connection, tx, "SELECT IsMe FROM Persons WHERE Id = $s;", p) == 1;
        Stories.StoryGroupStore.MovePersonStories(connection, tx, sourceId, targetId); // Storys (ADR-0058): Zustand nicht per Kaskade verlieren
        Execute(connection, tx, "DELETE FROM Persons WHERE Id = $s;", p);
        if (sourceIsMe)
        {
            Execute(connection, tx, """
                UPDATE Persons SET IsMe = 1
                WHERE Id = $t AND NOT EXISTS (SELECT 1 FROM Persons o WHERE o.GroupId = Persons.GroupId AND o.IsMe = 1);
                """, p);
        }
    }

    /// <summary>
    /// Getrennt gehaltene Person: Rufname „Name (Bibliothek)“, eindeutig in BEIDEN Gruppen (die Quelle zieht gleich mit in
    /// die Zielgruppe), Anzeigename folgt.
    /// </summary>
    private static void RenameKeptApart(SqliteConnection connection, SqliteTransaction tx, int personId, string name, string libraryName, int targetGroup, int sourceGroup)
    {
        var baseName = $"{name} ({(string.IsNullOrWhiteSpace(libraryName) ? "verknüpft" : libraryName.Trim())})";
        var candidate = baseName;
        for (var n = 2;
             Scalar(connection, tx, "SELECT COUNT(*) FROM Persons WHERE GroupId IN ($g, $s) AND Name = $n AND Id <> $p;",
                 ("$g", targetGroup), ("$s", sourceGroup), ("$n", candidate), ("$p", personId)) > 0;
             n++)
        {
            candidate = $"{baseName} {n}";
        }

        Execute(connection, tx, "UPDATE Persons SET Nickname = $n, Name = $n WHERE Id = $p;", ("$n", candidate), ("$p", personId));
    }

    // ---- Lösen ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Kopiert alle Begriffe und Personen von <paramref name="fromGroup"/> nach <paramref name="toGroup"/> und hängt die
    /// Zuordnungen der Dateien von <paramref name="libraryId"/> (Tags, Kategorie-Werte, Gesichter, Vorschläge, Ablehnungen)
    /// auf die Kopien um. Titelbilder: die Kopie behält ihres, wenn das Gesicht in der Bibliothek liegt; das Original
    /// verliert es in diesem Fall (die Leser nehmen dann das beste eigene Gesicht).
    /// </summary>
    public static LibraryUnlinkResult CopyTermsForLibrary(SqliteConnection connection, SqliteTransaction tx, int fromGroup, int toGroup, int libraryId)
    {
        var p = new (string, object)[] { ("$from", fromGroup), ("$to", toGroup), ("$lib", libraryId) };
        Execute(connection, tx, "DROP TABLE IF EXISTS temp._CopyMap;");
        Execute(connection, tx, "CREATE TEMP TABLE _CopyMap (Kind TEXT NOT NULL, OldId INTEGER NOT NULL, NewId INTEGER NOT NULL, PRIMARY KEY (Kind, OldId));");

        var tags = Execute(connection, tx, """
            INSERT INTO _CopyMap SELECT 'T', Id, (SELECT COALESCE(MAX(Id), 0) FROM Tags) + ROW_NUMBER() OVER (ORDER BY Id) FROM Tags WHERE GroupId = $from;
            """, p);
        Execute(connection, tx, """
            INSERT INTO Tags (Id, Name, Color, SortOrder, CreatedAtUtc, GroupId)
            SELECT m.NewId, t.Name, t.Color, t.SortOrder, t.CreatedAtUtc, $to FROM _CopyMap m JOIN Tags t ON t.Id = m.OldId WHERE m.Kind = 'T';
            """, p);

        var categories = Execute(connection, tx, """
            INSERT INTO _CopyMap SELECT 'C', Id, (SELECT COALESCE(MAX(Id), 0) FROM Categories) + ROW_NUMBER() OVER (ORDER BY Id) FROM Categories WHERE GroupId = $from;
            """, p);
        Execute(connection, tx, """
            INSERT INTO Categories (Id, Name, SortOrder, CreatedAtUtc, GroupId)
            SELECT m.NewId, c.Name, c.SortOrder, c.CreatedAtUtc, $to FROM _CopyMap m JOIN Categories c ON c.Id = m.OldId WHERE m.Kind = 'C';
            INSERT INTO _CopyMap
            SELECT 'V', v.Id, (SELECT COALESCE(MAX(Id), 0) FROM CategoryValues) + ROW_NUMBER() OVER (ORDER BY v.Id)
            FROM CategoryValues v JOIN _CopyMap m ON m.Kind = 'C' AND m.OldId = v.CategoryId;
            INSERT INTO CategoryValues (Id, CategoryId, Name, SortOrder)
            SELECT vm.NewId, cm.NewId, v.Name, v.SortOrder
            FROM _CopyMap vm JOIN CategoryValues v ON v.Id = vm.OldId JOIN _CopyMap cm ON cm.Kind = 'C' AND cm.OldId = v.CategoryId
            WHERE vm.Kind = 'V';
            """, p);

        var relations = Execute(connection, tx, """
            INSERT INTO _CopyMap SELECT 'R', Id, (SELECT COALESCE(MAX(Id), 0) FROM PersonRelations) + ROW_NUMBER() OVER (ORDER BY Id) FROM PersonRelations WHERE GroupId = $from;
            """, p);
        Execute(connection, tx, """
            INSERT INTO PersonRelations (Id, Name, GroupId)
            SELECT m.NewId, r.Name, $to FROM _CopyMap m JOIN PersonRelations r ON r.Id = m.OldId WHERE m.Kind = 'R';
            """, p);

        var persons = Execute(connection, tx, """
            INSERT INTO _CopyMap SELECT 'P', Id, (SELECT COALESCE(MAX(Id), 0) FROM Persons) + ROW_NUMBER() OVER (ORDER BY Id) FROM Persons WHERE GroupId = $from;
            """, p);
        Execute(connection, tx, """
            INSERT INTO Persons (Id, Name, CreatedAtUtc, CoverFaceId, IsHidden, FirstName, LastName, Nickname, RelationId,
                                 BirthYear, BirthMonth, BirthDay, IsMe, GroupId)
            SELECT m.NewId, x.Name, x.CreatedAtUtc,
                   CASE WHEN EXISTS (SELECT 1 FROM Faces fa JOIN Files f ON f.Id = fa.FileId
                                     WHERE fa.Id = x.CoverFaceId AND f.LibraryId = $lib) THEN x.CoverFaceId END,
                   x.IsHidden, x.FirstName, x.LastName, x.Nickname,
                   (SELECT r.NewId FROM _CopyMap r WHERE r.Kind = 'R' AND r.OldId = x.RelationId),
                   x.BirthYear, x.BirthMonth, x.BirthDay, x.IsMe, $to
            FROM _CopyMap m JOIN Persons x ON x.Id = m.OldId WHERE m.Kind = 'P';
            UPDATE Persons SET CoverFaceId = NULL
            WHERE GroupId = $from AND CoverFaceId IN (SELECT fa.Id FROM Faces fa JOIN Files f ON f.Id = fa.FileId WHERE f.LibraryId = $lib);
            """, p);

        // Zuordnungen der herausgelösten Bibliothek auf die Kopien umhängen.
        Execute(connection, tx, """
            UPDATE FileTags SET TagId = (SELECT m.NewId FROM _CopyMap m WHERE m.Kind = 'T' AND m.OldId = FileTags.TagId)
            WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib)
              AND TagId IN (SELECT OldId FROM _CopyMap WHERE Kind = 'T');
            UPDATE FileCategoryValues SET
                CategoryId = (SELECT m.NewId FROM _CopyMap m WHERE m.Kind = 'C' AND m.OldId = FileCategoryValues.CategoryId),
                ValueId = (SELECT m.NewId FROM _CopyMap m WHERE m.Kind = 'V' AND m.OldId = FileCategoryValues.ValueId)
            WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib)
              AND CategoryId IN (SELECT OldId FROM _CopyMap WHERE Kind = 'C');
            UPDATE Faces SET PersonId = (SELECT m.NewId FROM _CopyMap m WHERE m.Kind = 'P' AND m.OldId = Faces.PersonId)
            WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib)
              AND PersonId IN (SELECT OldId FROM _CopyMap WHERE Kind = 'P');
            UPDATE Faces SET SuggestedPersonId = (SELECT m.NewId FROM _CopyMap m WHERE m.Kind = 'P' AND m.OldId = Faces.SuggestedPersonId)
            WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib)
              AND SuggestedPersonId IN (SELECT OldId FROM _CopyMap WHERE Kind = 'P');
            UPDATE FaceRejections SET PersonId = (SELECT m.NewId FROM _CopyMap m WHERE m.Kind = 'P' AND m.OldId = FaceRejections.PersonId)
            WHERE FaceId IN (SELECT fa.Id FROM Faces fa JOIN Files f ON f.Id = fa.FileId WHERE f.LibraryId = $lib)
              AND PersonId IN (SELECT OldId FROM _CopyMap WHERE Kind = 'P');
            """, p);

        // Motiv-Begriffe (0.2.56): Kopien mit Text-Vektor (die Treffer bleiben gültig), Anker = die herausgelöste
        // Bibliothek; ihre Treffer zeigen auf die Kopien. Die Originale bekommen einen Anker in der alten Gruppe.
        var motifTerms = Execute(connection, tx, """
            INSERT INTO _CopyMap SELECT 'M', Id, (SELECT COALESCE(MAX(Id), 0) FROM MotifTerms) + ROW_NUMBER() OVER (ORDER BY Id) FROM MotifTerms WHERE GroupId = $from;
            """, p);
        Execute(connection, tx, """
            INSERT INTO MotifTerms (Id, LibraryId, GroupId, Name, Prompt, Threshold, SortOrder, TextEmbedding, TextModelVersion, CreatedAtUtc)
            SELECT m.NewId, $lib, $to, t.Name, t.Prompt, t.Threshold, t.SortOrder, t.TextEmbedding, t.TextModelVersion, t.CreatedAtUtc
            FROM _CopyMap m JOIN MotifTerms t ON t.Id = m.OldId WHERE m.Kind = 'M';
            UPDATE FileMotifs SET TermId = (SELECT m.NewId FROM _CopyMap m WHERE m.Kind = 'M' AND m.OldId = FileMotifs.TermId)
            WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib)
              AND TermId IN (SELECT OldId FROM _CopyMap WHERE Kind = 'M');
            """, p);
        ReanchorMotifTerms(connection, tx, libraryId);
        if (Scalar(connection, tx, "SELECT COUNT(*) FROM Settings WHERE Key = $k;", ("$k", MotifSeededKey(fromGroup))) > 0)
        {
            Execute(connection, tx, "INSERT OR IGNORE INTO Settings (Key, Value) VALUES ($k, 'true');", ("$k", MotifSeededKey(toGroup)));
        }

        Stories.StoryGroupStore.CopyForLibrary(connection, tx, fromGroup, toGroup, libraryId); // Storys (ADR-0058), braucht noch _CopyMap (Personen)
        Execute(connection, tx, "DROP TABLE temp._CopyMap;");
        Albums.AlbumGroupStore.CopyForLibrary(connection, tx, fromGroup, toGroup, libraryId); // Alben (ADR-0054)
        Places.Tracks.GpsTrackGroupStore.CopyForLibrary(connection, tx, fromGroup, toGroup); // GPS-Spuren (ADR-0062): Kopie für die gelöste Bibliothek
        return new LibraryUnlinkResult(toGroup, tags, categories, relations, persons, motifTerms);
    }

    private static List<(int Source, int Target)> ReadMap(SqliteConnection connection, SqliteTransaction tx, string kind)
    {
        using var cmd = Command(connection, tx, "SELECT SourceId, TargetId FROM _LinkMap WHERE Kind = $k;", ("$k", kind));
        var list = new List<(int, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return list;
    }
}
