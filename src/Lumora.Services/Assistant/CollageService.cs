using System.Globalization;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Quality;
using Lumora.Services.Stories;

namespace Lumora.Services.Assistant;

/// <summary>Einstellungen des Dialogs „Als Collage-Bild speichern“.</summary>
public sealed record CollageOptions(
    CollageFormat Format = CollageFormat.Wide16x9,
    CollageSize Size = CollageSize.Uhd4K,
    int Count = 36,
    CollageArrangement Arrangement = CollageArrangement.Hero,
    bool ShowTitle = true,
    bool ShowDates = false)
{
    public static IReadOnlyList<int> CountChoices { get; } = [12, 24, 36, 48];
}

/// <summary>Ein Foto der Collage (Original-Pfad, Seitenverhältnis nach EXIF-Drehung, Aufnahmezeit in Ortszeit).</summary>
public sealed record CollagePhoto(int FileId, string Path, double Aspect, DateTime? LocalTime);

/// <summary>Dateiformat nach Endung: .png → PNG, sonst JPEG.</summary>
public enum CollageImageFormat
{
    Jpeg = 0,
    Png = 1,
}

/// <summary>Auftrag an den Renderer: Layout, Fotos je Platz (Index), sachliche Überschrift, Ziel.</summary>
public sealed record CollageRenderJob(
    CollageLayoutResult Layout,
    IReadOnlyList<CollagePhoto> Photos,
    string? Title,
    string? TitleNote,
    bool ShowDates,
    string OutputPath,
    CollageImageFormat Format);

/// <summary>
/// Zeichnet die Collage aus den ORIGINALEN in voller Qualität (Dekodier-Größe je Platz, EXIF-Ausrichtung) — WIC/WPF lebt im
/// Wpf-Projekt hinter diesem Vertrag (CLAUDE.md: keine WPF-Referenz in Services). Schreibt zuerst eine Temp-Datei neben dem
/// Ziel (im selben Ordner, also im Container einer versteckten Bibliothek) und benennt sie dann um.
/// </summary>
public interface ICollageRenderer
{
    Task RenderAsync(CollageRenderJob job, IProgress<int>? progress, CancellationToken ct);
}

/// <summary>Gespeicherte Collage.</summary>
public sealed record CollageSaveResult(string Path, int Width, int Height, int Photos);

/// <summary>Collage-Bild: Fotos laden, gute auswählen und mischen, Speicherort, Rendern (ADR-0063).</summary>
public interface ICollageService
{
    /// <summary>Fotos (keine Videos, nicht fehlend) mit Pfad und Seitenverhältnis — nur im aktuellen Bereich, Reihenfolge wie übergeben.</summary>
    Task<IReadOnlyList<CollagePhoto>> LoadPhotosAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>Auswahl-Vorrat: bis zu <paramref name="pool"/> gute Fotos (Schärfe, Gesichter, keine Doppelten), bestes zuerst.</summary>
    Task<IReadOnlyList<int>> RankAsync(IReadOnlyList<int> fileIds, int pool, CancellationToken ct = default);

    /// <summary>Standardordner: Bilder\Lumora\Collagen bzw. in einer versteckten Bibliothek „Collagen“ im Container.</summary>
    Task<string> GetDefaultFolderAsync(CancellationToken ct = default);

    /// <summary>In einer versteckten Bibliothek nur Ziele im Container (unter einem Bibliotheksordner); sonst alles.</summary>
    Task<bool> IsAllowedTargetAsync(string path, CancellationToken ct = default);

    /// <summary>Rendert und speichert; <paramref name="photos"/> in Platz-Reihenfolge.</summary>
    Task<CollageSaveResult> SaveAsync(CollageOptions options, IReadOnlyList<CollagePhoto> photos, string? title, string path, IProgress<int>? progress = null, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class CollageService(
    ILibraryContextFactory factory,
    ILibraryScope scope,
    IHighlightSelector selector,
    ICollageRenderer renderer,
    ILogger<CollageService> logger,
    ICatalogStoragePaths? catalog = null,
    StoryClock? clock = null) : ICollageService
{
    public const string FolderName = "Collagen";

    private readonly StoryClock _clock = clock ?? new StoryClock();

    public Task<IReadOnlyList<CollagePhoto>> LoadPhotosAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<CollagePhoto>>(async () =>
        {
            var current = await scope.GetCurrentAsync(ct).ConfigureAwait(false);
            if (fileIds.Count == 0 || !current.HasLibrary)
            {
                return [];
            }

            using var connection = factory.OpenSqliteConnection();
            var found = new Dictionary<int, CollagePhoto>();
            foreach (var chunk in fileIds.Distinct().Chunk(500))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT f.Id, d.Path, f.Name, f.Width, f.Height, f.CapturedAt, f.CapturedAtOffset
                    FROM Files f CROSS JOIN Folders d ON d.Id = f.FolderId
                    WHERE f.Id IN ({AssistantStore.IdList(chunk)}) AND f.LibraryId IN ({current.SqlLibraryIdList}) AND f.MediaType = 0 AND f.Missing = 0;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var w = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                    var h = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                    DateTime? local = null;
                    if (!reader.IsDBNull(5))
                    {
                        var utc = reader.GetInt64(5);
                        var offset = reader.IsDBNull(6)
                            ? _clock.Zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(utc))
                            : TimeSpan.FromMinutes(reader.GetInt32(6));
                        local = DateTimeOffset.FromUnixTimeSeconds(utc).UtcDateTime + offset;
                    }

                    found[reader.GetInt32(0)] = new CollagePhoto(reader.GetInt32(0), Path.Combine(reader.GetString(1), reader.GetString(2)),
                        w > 0 && h > 0 ? (double)w / h : CollageLayout.TargetCellAspect, local);
                }
            }

            return fileIds.Distinct().Where(found.ContainsKey).Select(id => found[id]).ToList();
        }, ct);

    public async Task<IReadOnlyList<int>> RankAsync(IReadOnlyList<int> fileIds, int pool, CancellationToken ct = default)
    {
        if (fileIds.Count == 0 || pool <= 0)
        {
            return [];
        }

        // nur Fotos des Bereichs (Videos kommen nicht in die Collage — sonst schrumpfte der Vorrat)
        var photos = (await LoadPhotosAsync(fileIds, ct).ConfigureAwait(false)).Select(p => p.FileId).ToList();
        if (photos.Count == 0)
        {
            return [];
        }

        var input = StoryAssistantService.Sample(photos, StoryAssistantService.MaxHighlightInput);
        var want = Math.Min(pool, input.Count);
        var picks = await selector.SelectAsync(input, new HighlightOptions((double)want / input.Count, want, want), ct).ConfigureAwait(false);
        var ranked = picks.OrderByDescending(p => p.Score).Select(p => p.FileId).ToList();
        if (ranked.Count < want)
        {
            // weniger Gruppen als gewünscht (viele ähnliche): mit den übrigen auffüllen
            ranked.AddRange(input.Where(id => !ranked.Contains(id)).Take(want - ranked.Count));
        }

        return ranked;
    }

    public async Task<string> GetDefaultFolderAsync(CancellationToken ct = default)
    {
        if (catalog?.IsHidden == true)
        {
            var roots = await ReadLibraryRootsAsync(ct).ConfigureAwait(false);
            return roots.Count > 0 ? Path.Combine(roots[0], FolderName) : string.Empty;
        }

        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.Combine(string.IsNullOrEmpty(pictures) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : pictures, "Lumora", FolderName);
    }

    public async Task<bool> IsAllowedTargetAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (catalog?.IsHidden != true)
        {
            return true;
        }

        var full = Path.GetFullPath(path);
        var roots = await ReadLibraryRootsAsync(ct).ConfigureAwait(false);
        return roots.Any(root =>
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        });
    }

    public async Task<CollageSaveResult> SaveAsync(CollageOptions options, IReadOnlyList<CollagePhoto> photos, string? title, string path, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count == 0)
        {
            throw new InvalidOperationException("Keine Fotos für die Collage.");
        }

        if (!await IsAllowedTargetAsync(path, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("In einer versteckten Bibliothek lässt sich die Collage nur im Container speichern (im Bibliotheksordner).");
        }

        var target = Path.GetFullPath(path);
        var format = string.Equals(Path.GetExtension(target), ".png", StringComparison.OrdinalIgnoreCase) ? CollageImageFormat.Png : CollageImageFormat.Jpeg;
        var (width, height, dpi) = CollageLayout.Dimensions(options.Format, options.Size);
        var showTitle = options.ShowTitle && !string.IsNullOrWhiteSpace(title);
        var layout = CollageLayout.Compute(width, height, dpi, photos.Select(p => p.Aspect).ToList(), options.Arrangement, showTitle);
        var note = photos.Count.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + (photos.Count == 1 ? " Foto" : " Fotos");
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
        var started = System.Diagnostics.Stopwatch.StartNew();
        await renderer.RenderAsync(new CollageRenderJob(layout, photos, showTitle ? title!.Trim() : null, showTitle ? note : null, options.ShowDates, target, format), progress, ct).ConfigureAwait(false);
        logger.LogInformation("Assistent: Collage gespeichert ({Width} × {Height}, {Count} Fotos, {Ms} ms)", width, height, photos.Count, started.ElapsedMilliseconds);
        return new CollageSaveResult(target, width, height, photos.Count);
    }

    /// <summary>Dateiname aus der Überschrift (ohne verbotene Zeichen): „Anna · 1985–2005“ → „Anna 1985-2005.jpg“.</summary>
    public static string SuggestFileName(string? title)
    {
        var text = string.IsNullOrWhiteSpace(title) ? "Collage" : title.Replace(StoryTitleBuilder.Dot, " ", StringComparison.Ordinal).Replace('–', '-');
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(text.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        while (clean.Contains("  ", StringComparison.Ordinal))
        {
            clean = clean.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (clean.Length > 80)
        {
            clean = clean[..80].TrimEnd();
        }

        return (clean.Length == 0 ? "Collage" : clean) + ".jpg";
    }

    private Task<List<string>> ReadLibraryRootsAsync(CancellationToken ct)
        => Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT RootPath AS P, 0 AS O FROM Libraries UNION SELECT Path, 1 FROM LibraryRoots ORDER BY O, P;";
            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && reader.GetString(0) is { Length: > 0 } p)
                {
                    list.Add(p);
                }
            }

            return list;
        }, ct);
}
