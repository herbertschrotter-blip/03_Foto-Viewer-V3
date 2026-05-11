using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Scanning;

/// <summary>
/// Durchläuft das Filesystem rekursiv unterhalb eines RootPath und liefert DiscoveredFile-Einträge
/// für JPG/PNG/MP4. Permission-/IO-Fehler werden geloggt und übersprungen (kein Abbruch des Scans).
/// V0.1-Filter: .jpg/.jpeg/.png/.mp4 (siehe PLAN Constraints).
/// </summary>
public sealed class FolderEnumerator
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
    };

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false, // wir machen den Rekurs manuell, um Permission-Fehler pro Folder zu behandeln
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    private readonly ILogger<FolderEnumerator> _logger;

    public FolderEnumerator(ILogger<FolderEnumerator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Yield-Stream über alle gefundenen MediaFiles unterhalb von rootPath.
    /// Berücksichtigt Cancellation pro Folder.
    /// </summary>
    public IEnumerable<DiscoveredFile> Enumerate(string rootPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(rootPath))
        {
            _logger.LogWarning("RootPath existiert nicht: {Path}", rootPath);
            yield break;
        }

        var queue = new Queue<string>();
        queue.Enqueue(rootPath);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = queue.Dequeue();

            // Files in diesem Folder
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", Options);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Keine Berechtigung auf Folder: {Path}", folder);
                continue;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO-Fehler auf Folder: {Path}", folder);
                continue;
            }

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();
                DiscoveredFile? df = null;
                try
                {
                    var ext = Path.GetExtension(filePath);
                    if (!TryClassify(ext, out var mediaType))
                    {
                        continue;
                    }

                    var info = new FileInfo(filePath);
                    df = new DiscoveredFile(
                        FullPath: info.FullName,
                        FolderPath: folder,
                        Name: info.Name,
                        Extension: ext.ToLowerInvariant(),
                        Size: info.Length,
                        CreatedAtUtc: info.CreationTimeUtc,
                        ModifiedAtUtc: info.LastWriteTimeUtc,
                        MediaType: mediaType);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Skip File: {Path}", filePath);
                }

                if (df is not null)
                {
                    yield return df;
                }
            }

            // Subfolder enqueuen
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(folder, "*", Options))
                {
                    queue.Enqueue(sub);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Keine Berechtigung auf Subfolder-Listing: {Path}", folder);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO-Fehler auf Subfolder-Listing: {Path}", folder);
            }
        }
    }

    private static bool TryClassify(string extension, out MediaType mediaType)
    {
        if (ImageExtensions.Contains(extension))
        {
            mediaType = MediaType.Image;
            return true;
        }

        if (VideoExtensions.Contains(extension))
        {
            mediaType = MediaType.Video;
            return true;
        }

        mediaType = default;
        return false;
    }
}
