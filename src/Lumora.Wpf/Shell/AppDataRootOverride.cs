using System;
using System.IO;
using Lumora.Services.Infrastructure;

namespace Lumora.Shell;

/// <summary>
/// Optionaler AppData-Root per Umgebungsvariable LUMORA_APPDATA (z. B. für Rauchtests mit frischer DB
/// ohne die Katalog-DB des Benutzers anzufassen). Ohne Variable gilt %LocalAppData%\Lumora. In beiden Fällen
/// bestimmt <c>database-location.json</c> in der Wurzel, wo DB und Thumbnails liegen (ADR-0026).
/// </summary>
public static class AppDataRootOverride
{
    public const string EnvironmentVariable = "LUMORA_APPDATA";

    /// <summary>true, wenn <see cref="EnvironmentVariable"/> gesetzt ist (dann kein Umzug aus dem alten Ordner, ADR-0056).</summary>
    public static bool IsSet => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <param name="defaultRoot">Wurzel ohne Variable; null = <see cref="AppDataPaths.DefaultAppDataRoot"/>.</param>
    public static AppDataPaths Resolve(string? defaultRoot = null)
    {
        var root = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return AppDataPaths.Resolve(string.IsNullOrWhiteSpace(root) ? defaultRoot ?? AppDataPaths.DefaultAppDataRoot : Path.GetFullPath(root));
    }
}
