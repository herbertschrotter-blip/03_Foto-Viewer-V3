using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoViewerV3.Services.Sorter;

/// <summary>
/// Pfad- und Namenshelfer des Sortierers: Normalisierung wie im FileOperationService,
/// Path-Traversal-Schutz für Ordnernamen (V2: kein „/“, „\“ oder „..“), Größenformatierung.
/// </summary>
public static class SorterPaths
{
    private const int MaxFolderNameLength = 200;

    private static readonly Regex s_resultSubKey = new("^result_(\\d+)$", RegexOptions.CultureInvariant);

    /// <summary>Voller Pfad ohne abschließende Trennzeichen; Laufwerks-/UNC-Wurzeln behalten ihres („E:\“).</summary>
    public static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>true, wenn <paramref name="normalizedPath"/> gleich <paramref name="normalizedRoot"/> ist oder darunter liegt.</summary>
    public static bool IsInsideRoot(string normalizedPath, string normalizedRoot)
    {
        if (PathEquals(normalizedPath, normalizedRoot))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Relativer Pfad unterhalb der Wurzel („.“ für die Wurzel selbst); außerhalb → voller Pfad.</summary>
    public static string RelativeTo(string normalizedPath, string normalizedRoot)
    {
        if (PathEquals(normalizedPath, normalizedRoot))
        {
            return ".";
        }

        return IsInsideRoot(normalizedPath, normalizedRoot)
            ? normalizedPath[normalizedRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : normalizedPath;
    }

    /// <summary>Anzahl der Pfadsegmente (für „tiefste Ordner zuerst“).</summary>
    public static int Depth(string path)
        => path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Prüft einen einzelnen Ordnernamen (ein Segment): kein Trennzeichen, kein „..“, keine
    /// ungültigen Zeichen, nicht leer, nicht nur Punkte, kein abschließender Punkt/Leerraum.
    /// </summary>
    public static bool IsValidFolderName(string? name, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Ordnername fehlt.";
            return false;
        }

        if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            error = "Ungültiger Ordnername (Path-Traversal).";
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Ordnername enthält ungültige Zeichen.";
            return false;
        }

        if (name.Trim('.').Length == 0 || name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
        {
            error = "Ordnername ist ungültig.";
            return false;
        }

        if (name.Length > MaxFolderNameLength)
        {
            error = $"Ordnername ist zu lang (max. {MaxFolderNameLength} Zeichen).";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Macht aus einem Gruppenschlüssel einen gültigen Ordnernamen-Vorschlag: ungültige Zeichen
    /// und Trennzeichen → „_“, Leerraum und Punkte am Rand entfernt; leer → „_gruppe“.
    /// Gültige Schlüssel bleiben unverändert (V2: Vorschlag = Schlüssel).
    /// </summary>
    public static string SanitizeFolderName(string key)
    {
        if (IsValidFolderName(key, out _))
        {
            return key;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = key.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        var name = new string(chars).Replace("..", "_").Trim().TrimEnd('.', ' ');
        if (name.Length > MaxFolderNameLength)
        {
            name = name[..MaxFolderNameLength].TrimEnd('.', ' ');
        }

        return name.Trim('.', '_', ' ').Length == 0 ? "_gruppe" : name;
    }

    /// <summary>V2 <c>Convert-SubKeyToShort</c>: result → r0, result_N → rN, _other → other, sonst unverändert.</summary>
    public static string ToShortSubKey(string subKey)
    {
        if (string.Equals(subKey, "result", StringComparison.OrdinalIgnoreCase))
        {
            return "r0";
        }

        var m = s_resultSubKey.Match(subKey.ToLowerInvariant());
        if (m.Success)
        {
            return "r" + m.Groups[1].Value;
        }

        if (string.Equals(subKey, SorterGroup.OtherKey, StringComparison.OrdinalIgnoreCase))
        {
            return "other";
        }

        return subKey;
    }

    /// <summary>Bytes menschenlesbar (wie V2 Format-FileSize: GB/MB mit einer Nachkommastelle, KB ganzzahlig).</summary>
    public static string FormatSize(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;
        var culture = CultureInfo.CurrentCulture;
        if (bytes >= gb)
        {
            return string.Format(culture, "{0:N1} GB", bytes / (double)gb);
        }

        if (bytes >= mb)
        {
            return string.Format(culture, "{0:N1} MB", bytes / (double)mb);
        }

        if (bytes >= kb)
        {
            return string.Format(culture, "{0:N0} KB", bytes / (double)kb);
        }

        return string.Format(culture, "{0} Bytes", bytes);
    }

    /// <summary>true, wenn der Ordner existiert und keinerlei Einträge (auch keine versteckten) enthält.</summary>
    public static bool IsTrulyEmptyDirectory(string path)
    {
        try
        {
            return Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
