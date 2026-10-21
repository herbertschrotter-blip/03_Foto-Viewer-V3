namespace Lumora.Services.HiddenLibraries;

/// <summary>Laufwerkswurzeln, die nach versteckten Bibliotheken durchsucht werden (Tests: feste Liste).</summary>
public interface IDriveRootSource
{
    IReadOnlyList<string> GetSearchRoots();
}

/// <summary>
/// Eingehängte lokale Laufwerke („Fixed“ und RAM-Disks), die bereit sind. Netzlaufwerke, CD/DVD und Wechselmedien werden
/// nicht angefasst (nicht wecken, keine Wartezeiten). VeraCrypt- und VHDX-Laufwerke erscheinen als „Fixed“; ein
/// VeraCrypt-Volume mit der Option „Als Wechselmedium einhängen“ wird deshalb nicht gefunden (README).
/// </summary>
public sealed class LocalDriveRootSource : IDriveRootSource
{
    public IReadOnlyList<string> GetSearchRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Ram && drive.IsReady)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Laufwerk verschwindet gerade (Aushängen) — überspringen
            }
        }

        return roots;
    }
}

/// <summary>
/// Sucht versteckte Bibliotheken (ADR-0049): auf jedem Laufwerk aus <see cref="IDriveRootSource"/> in der Wurzel und
/// genau eine Ebene darunter nach <c>.lumora\hidden-library.json</c>. Schnell (keine Rekursion) und still: protokolliert
/// nichts — schon „gesucht“ wäre im normalen Protokoll eine Spur. Merkt sich nichts.
/// </summary>
public sealed class HiddenLibraryLocator
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        // System (z. B. „System Volume Information“, Papierkorb) und Verknüpfungspunkte überspringen; versteckte nicht
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private readonly IDriveRootSource _drives;

    public HiddenLibraryLocator(IDriveRootSource drives)
    {
        _drives = drives;
    }

    public Task<IReadOnlyList<HiddenLibraryInfo>> FindAsync(CancellationToken ct = default)
        => Task.Run(() => Find(ct), ct);

    public IReadOnlyList<HiddenLibraryInfo> Find(CancellationToken ct = default)
    {
        var found = new List<HiddenLibraryInfo>();
        foreach (var root in _drives.GetSearchRoots())
        {
            ct.ThrowIfCancellationRequested();
            TryAdd(root, found);
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root, "*", Options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                if (!Infrastructure.CatalogFolders.IsCatalogFolder(child))
                {
                    TryAdd(child, found);
                }
            }
        }

        return found
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(l => l.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void TryAdd(string folder, List<HiddenLibraryInfo> found)
    {
        if (HiddenLibraryStore.TryLoad(folder) is { } manifest)
        {
            found.Add(new HiddenLibraryInfo(Path.GetFullPath(folder), manifest.Name));
        }
    }
}

/// <summary>Regeln für den Ordner einer neuen versteckten Bibliothek (Anlegen-Dialog, ADR-0049).</summary>
public static class HiddenLibraryFolderRules
{
    /// <summary>
    /// null = geeignet, sonst die Meldung. Der Ordner muss eine Laufwerkswurzel oder eine Ebene darunter sein (sonst findet
    /// die Suche ihn nicht wieder), lokal (kein UNC-Pfad), nicht in OneDrive, nicht im Lumora-Datenordner und frei von einer
    /// versteckten Bibliothek.
    /// </summary>
    /// <param name="forbiddenFolders">Ordner, in denen bzw. unter denen er nicht liegen darf (Lumora-Datenordner, OneDrive …).</param>
    /// <param name="maxDepth">Ebenen unter der Laufwerkswurzel; nur der Rauchtest (Temp-Ordner als „Container“) erlaubt mehr.</param>
    public static string? Validate(string? folder, IEnumerable<string> forbiddenFolders, int maxDepth = 1)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return "Bitte ein Laufwerk wählen.";
        }

        var full = Path.GetFullPath(folder);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "Versteckte Bibliotheken liegen auf einem lokal eingehängten Laufwerk (kein Netzwerkpfad).";
        }

        var relative = full[root.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Length > maxDepth)
        {
            return "Der Ordner muss direkt auf dem Laufwerk liegen (z. B. X:\\ oder X:\\Fotos) — sonst findet Lumora ihn nicht wieder.";
        }

        if (Infrastructure.CatalogFolders.IsCatalogFolder(full))
        {
            return "Dieser Ordner ist der Katalogordner einer versteckten Bibliothek.";
        }

        foreach (var forbidden in forbiddenFolders.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            if (Infrastructure.AppDataPaths.IsSameOrInside(full, forbidden) || Infrastructure.AppDataPaths.IsSameOrInside(forbidden, full))
            {
                return "Dieser Ordner ist nicht geeignet (Lumora-Datenordner, OneDrive oder ein Ordner einer normalen Bibliothek). Den Container nie in OneDrive ablegen.";
            }
        }

        if (HiddenLibraryStore.Exists(full))
        {
            return "In diesem Ordner liegt bereits eine versteckte Bibliothek.";
        }

        return null;
    }

    /// <summary>OneDrive-Ordner aus den Umgebungsvariablen (privat und geschäftlich).</summary>
    public static IReadOnlyList<string> OneDriveFolders()
        => new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p!))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
