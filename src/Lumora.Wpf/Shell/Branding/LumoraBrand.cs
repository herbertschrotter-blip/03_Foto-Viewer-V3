namespace Lumora.Shell.Branding;

/// <summary>
/// Produktname und Ressourcen-Schlüssel der Marke „Lumora“ (ADR-0047, Mockup 07). Seit 0.2.49 heißt die App sichtbar
/// „Lumora“ und die Programmdatei <c>Lumora.exe</c>; Projekt-/Namespace-Namen (<c>Lumora.*</c>), der Datenordner
/// <c>%LocalAppData%\Lumora</c>, <c>database-location.json</c>, die Umgebungsvariablen <c>LUMORA_*</c> und
/// der Protokollname <c>lumora-*.log</c> bleiben bewusst unverändert (Kompatibilität mit vorhandenen Katalogen).
/// </summary>
public static class LumoraBrand
{
    /// <summary>Sichtbarer Produktname (Fenstertitel, Taskleiste, Info, Dialogtexte).</summary>
    public const string ProductName = "Lumora";

    /// <summary>Früherer Name bis 0.2.48 (Info › Vorgänger, README).</summary>
    public const string FormerProductName = "PhotoViewer V3";

    /// <summary>Letzte Version unter dem früheren Namen.</summary>
    public const string LastFormerNameVersion = "0.2.48";

    /// <summary>Name der Programmdatei (AssemblyName im csproj).</summary>
    public const string ExecutableName = "Lumora.exe";

    /// <summary>Verlaufspinsel des Schriftzugs (relativ 0..1, Indigo → Violett → Koralle → Bernstein), je Farbschema.</summary>
    public const string WordmarkBrushKey = "LumoraWordmarkBrush";

    /// <summary>Verlaufspinsel des Linsenrings im „o“ (Violett → Koralle), je Farbschema.</summary>
    public const string LensRingBrushKey = "LumoraLensRingBrush";

    /// <summary>Fenstertitel eines Nebenfensters mit Produktname, z. B. „IMG_0001.jpg – Lumora“.</summary>
    public static string WindowTitle(string? subject)
        => string.IsNullOrWhiteSpace(subject) ? ProductName : $"{subject} – {ProductName}";
}

/// <summary>Binding-Konverter für Fenstertitel: „Betreff“ → „Betreff – Lumora“ (<see cref="LumoraBrand.WindowTitle"/>).</summary>
public sealed class LumoraWindowTitleConverter : System.Windows.Data.IValueConverter
{
    public static LumoraWindowTitleConverter Instance { get; } = new();

    public object Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => LumoraBrand.WindowTitle(value as string);

    public object ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
