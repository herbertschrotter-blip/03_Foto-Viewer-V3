using Microsoft.Data.Sqlite;
using Lumora.Services.Infrastructure;

namespace Lumora.Services.Database.Relocation;

/// <summary>
/// Dateiliste des Thumbnail-Ordners aus einem einzigen Durchlauf (ADR-0026): dient der Platzprüfung und als
/// Kopierliste, damit der Ordner (bei 500k Dateien spürbar) nicht für Prüfung, erneute Prüfung und Kopie mehrfach
/// durchlaufen wird. <see cref="CatchUp"/> ergänzt nach der Pause der Thumbnail-Worker, was seit dem Durchlauf
/// entstanden ist — aus der DB, ohne den Ordner erneut zu lesen.
/// </summary>
internal sealed class ThumbnailInventory
{
    /// <summary>Sicherheitsabstand für den Abgleich: DB-Zeitstempel sind Unix-Sekunden.</summary>
    private const long CatchUpMarginSeconds = 2;

    private ThumbnailInventory(string root, DateTime takenAtUtc)
    {
        Root = root;
        TakenAtUtc = takenAtUtc;
    }

    /// <summary>Durchlaufener Thumbnail-Ordner.</summary>
    public string Root { get; }

    /// <summary>Beginn des Durchlaufs.</summary>
    public DateTime TakenAtUtc { get; }

    /// <summary>Zu kopierende Dateien relativ zu <see cref="Root"/> (ohne <c>.tmp</c>).</summary>
    public List<string> Files { get; } = [];

    /// <summary>Größe aller Dateien im Ordner, <c>.tmp</c> eingeschlossen (Platzprüfung wie bisher).</summary>
    public long Bytes { get; private set; }

    public static ThumbnailInventory Empty(string root) => new(root, DateTime.UtcNow);

    /// <summary>Durchläuft <paramref name="root"/> genau einmal.</summary>
    public static ThumbnailInventory Take(string root, CancellationToken ct)
    {
        var inventory = new ThumbnailInventory(root, DateTime.UtcNow);
        if (!Directory.Exists(root))
        {
            return inventory;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var seen = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            if (++seen % 1000 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            inventory.Bytes += file.Length;
            if (!IsTemp(file.FullName))
            {
                inventory.Files.Add(Path.GetRelativePath(root, file.FullName));
            }
        }

        return inventory;
    }

    /// <summary>
    /// Ergänzt Vorschaubilder, die seit dem Durchlauf erzeugt wurden: der Thumbnail-Dienst schreibt die Datei und
    /// danach die Zeile mit <c>GeneratedAtUtc</c> (vorher <c>LastAttemptAtUtc</c>). Erst nach
    /// <c>IThumbnailService.PauseAsync</c> aufrufen — dann entsteht nichts mehr. Gibt die Zahl ergänzter Dateien zurück.
    /// </summary>
    public int CatchUp(string databasePath, CancellationToken ct)
    {
        if (!File.Exists(databasePath))
        {
            return 0;
        }

        var since = new DateTimeOffset(DateTime.SpecifyKind(TakenAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds() - CatchUpMarginSeconds;
        var candidates = new List<string>();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = databasePath,
                   Mode = SqliteOpenMode.ReadWrite,
                   Pooling = false,
               }.ToString()))
        {
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT CachePath FROM Thumbnails WHERE GeneratedAtUtc >= $since OR LastAttemptAtUtc >= $since;";
            cmd.Parameters.AddWithValue("$since", since);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    candidates.Add(reader.GetString(0));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        ct.ThrowIfCancellationRequested();
        var known = new HashSet<string>(Files, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in candidates)
        {
            if (!Path.IsPathFullyQualified(path) || !AppDataPaths.IsSameOrInside(path, Root) || IsTemp(path))
            {
                continue;
            }

            var relative = Path.GetRelativePath(Root, path);
            var info = new FileInfo(path);
            if (!info.Exists || !known.Add(relative))
            {
                continue;
            }

            Files.Add(relative);
            Bytes += info.Length;
            added++;
        }

        return added;
    }

    private static bool IsTemp(string path) => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
}
