using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Services.Infrastructure;

/// <summary>
/// Gewählter Speicherort von Katalog-DB und Thumbnail-Cache (ADR-0026).
/// </summary>
/// <param name="DatabaseFolder">Ordner mit <c>library.db</c> und <c>thumbs\</c>; null = Standard (App-Wurzel).</param>
/// <param name="PendingDeletes">
/// Dateien und Ordner des alten Speicherorts, die nach einem Umzug beim nächsten Start gelöscht werden (die alte App-
/// Instanz hält beim Neustart noch Handles darauf).
/// </param>
public sealed record DatabaseLocation(string? DatabaseFolder, IReadOnlyList<string> PendingDeletes)
{
    public static DatabaseLocation Default { get; } = new(null, []);
}

/// <summary>
/// Liest und schreibt <see cref="DatabaseLocation"/> als kleine JSON-Datei in der App-Wurzel
/// (<c>%LocalAppData%\PhotoViewerV3\database-location.json</c>). Sie kann nicht in der DB selbst liegen — der Pfad
/// wird gebraucht, bevor die DB geöffnet wird. Fehlt die Datei, gilt der Standardort.
/// </summary>
public static class DatabaseLocationStore
{
    public const string FileName = "database-location.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetFilePath(string appDataRoot) => Path.Combine(appDataRoot, FileName);

    /// <summary>Liest die Datei; ohne Datei <see cref="DatabaseLocation.Default"/>. Beschädigt → <see cref="InvalidDataException"/>.</summary>
    public static DatabaseLocation Load(string appDataRoot)
    {
        var file = GetFilePath(appDataRoot);
        if (!File.Exists(file))
        {
            return DatabaseLocation.Default;
        }

        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(file), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Die Speicherort-Datei „{file}“ ist beschädigt: {ex.Message}", ex);
        }

        if (dto is null)
        {
            return DatabaseLocation.Default;
        }

        var folder = string.IsNullOrWhiteSpace(dto.DatabaseFolder) ? null : dto.DatabaseFolder;
        if (folder is not null && !Path.IsPathFullyQualified(folder))
        {
            throw new InvalidDataException($"Die Speicherort-Datei „{file}“ enthält keinen vollständigen Pfad: {folder}");
        }

        return new DatabaseLocation(folder, (dto.PendingDeletes ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList());
    }

    /// <summary>
    /// Schreibt atomar (Temp-Datei + Umbenennen). Standardort ohne offene Löschungen entfernt die Datei.
    /// </summary>
    public static void Save(string appDataRoot, DatabaseLocation location)
    {
        Directory.CreateDirectory(appDataRoot);
        var file = GetFilePath(appDataRoot);
        if (location.DatabaseFolder is null && location.PendingDeletes.Count == 0)
        {
            File.Delete(file);
            return;
        }

        var tmp = file + ".tmp";
        var json = JsonSerializer.Serialize(new Dto { DatabaseFolder = location.DatabaseFolder, PendingDeletes = location.PendingDeletes.ToList() }, JsonOptions);
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>
    /// Löscht die vorgemerkten Dateien und Ordner des alten Speicherorts (App-Start, Hintergrund). Was gerade noch
    /// gesperrt ist (die alte Instanz beendet sich noch), wird bis zu <paramref name="attempts"/>-mal nach
    /// <paramref name="retryDelay"/> erneut versucht; was dann noch bleibt, bleibt vorgemerkt. Nie gelöscht wird, was
    /// den aktiven Speicherort enthält oder darin liegt. Liefert die Anzahl entfernter Einträge.
    /// </summary>
    public static async Task<int> RunPendingDeletesAsync(
        IAppDataPaths paths,
        ILogger logger,
        int attempts = 3,
        TimeSpan? retryDelay = null,
        CancellationToken ct = default)
    {
        var location = Load(paths.AppDataRoot);
        if (location.PendingDeletes.Count == 0)
        {
            return 0;
        }

        var remaining = new List<string>();
        foreach (var entry in location.PendingDeletes)
        {
            if (TouchesActiveLocation(entry, paths))
            {
                logger.LogWarning("Alter Speicherort: {Path} gehört zum aktiven Speicherort und wird nicht gelöscht", entry);
                continue;
            }

            remaining.Add(entry);
        }

        var removed = 0;
        for (var attempt = 1; attempt <= Math.Max(1, attempts) && remaining.Count > 0; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }

            var failed = new List<string>();
            foreach (var entry in remaining)
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else if (File.Exists(entry))
                    {
                        File.Delete(entry);
                    }

                    removed++;
                    logger.LogInformation("Alter Speicherort: {Path} gelöscht", entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(entry);
                    if (attempt == attempts)
                    {
                        logger.LogWarning(ex, "Alter Speicherort: {Path} konnte nicht gelöscht werden — nächster Start versucht es erneut", entry);
                    }
                }
            }

            remaining = failed;
        }

        // Neu lesen und nur die erledigten Einträge austragen: das Löschen kann Minuten dauern, ein Umzug in der Zeit
        // hat inzwischen vielleicht einen neuen Ordner gespeichert.
        var done = location.PendingDeletes.Except(remaining, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = Load(paths.AppDataRoot);
        Save(paths.AppDataRoot, current with { PendingDeletes = current.PendingDeletes.Where(p => !done.Contains(p)).ToList() });
        return removed;
    }

    /// <summary>true, wenn der Eintrag die aktive DB, ihren Ordner oder den aktiven Thumbnail-Cache enthält oder darin liegt.</summary>
    internal static bool TouchesActiveLocation(string entry, IAppDataPaths paths)
    {
        if (!Path.IsPathFullyQualified(entry))
        {
            return true; // relative Einträge nie anfassen
        }

        return AppDataPaths.IsSameOrInside(paths.LibraryDatabasePath, entry)
               || AppDataPaths.IsSameOrInside(paths.ThumbnailsRoot, entry)
               || AppDataPaths.IsSameOrInside(entry, paths.ThumbnailsRoot)
               || AppDataPaths.IsSameOrInside(paths.AppDataRoot, entry)
               || AppDataPaths.PathsEqual(entry, Path.GetDirectoryName(paths.LibraryDatabasePath)!)
               || IsActiveDatabaseFile(entry, paths.LibraryDatabasePath);
    }

    private static bool IsActiveDatabaseFile(string entry, string databasePath)
        => AppDataPaths.PathsEqual(entry, databasePath)
           || AppDataPaths.PathsEqual(entry, databasePath + "-wal")
           || AppDataPaths.PathsEqual(entry, databasePath + "-shm");

    private sealed class Dto
    {
        public string? DatabaseFolder { get; set; }

        public List<string>? PendingDeletes { get; set; }
    }
}
