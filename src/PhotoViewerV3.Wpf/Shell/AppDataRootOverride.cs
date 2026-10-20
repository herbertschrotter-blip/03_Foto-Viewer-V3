using System;
using System.IO;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Optionaler AppData-Root per Umgebungsvariable PHOTOVIEWERV3_APPDATA (z. B. für Rauchtests mit frischer DB
/// ohne die Katalog-DB des Benutzers anzufassen). Ohne Variable gilt %LocalAppData%\PhotoViewerV3. In beiden Fällen
/// bestimmt <c>database-location.json</c> in der Wurzel, wo DB und Thumbnails liegen (ADR-0026).
/// </summary>
public static class AppDataRootOverride
{
    public const string EnvironmentVariable = "PHOTOVIEWERV3_APPDATA";

    public static AppDataPaths Resolve()
    {
        var root = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return AppDataPaths.Resolve(string.IsNullOrWhiteSpace(root) ? AppDataPaths.DefaultAppDataRoot : Path.GetFullPath(root));
    }
}
