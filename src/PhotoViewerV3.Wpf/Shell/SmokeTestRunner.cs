using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Library;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Rauchtest-Hook für unbeaufsichtigte Läufe (ohne Umgebungsvariable passiert nichts):
/// <list type="bullet">
/// <item>PHOTOVIEWERV3_SMOKE_ROOT = Ordner → Bibliothek darauf anlegen (falls nötig), Fresh-Scan, danach
/// ClearAndRescan, anschließend Ordnerbaum und Einstellungen anzeigen — über dieselben ViewModel-Pfade wie ein Klick.</item>
/// <item>PHOTOVIEWERV3_SMOKE_CLEANUP = 1 → die Bibliothek am Ende wieder aus dem Katalog entfernen (DeleteAsync).</item>
/// </list>
/// Zusammen mit PHOTOVIEWERV3_APPDATA (eigener AppData-Root) läuft der Test gegen eine frische DB.
/// </summary>
public static class SmokeTestRunner
{
    public const string RootVariable = "PHOTOVIEWERV3_SMOKE_ROOT";
    public const string CleanupVariable = "PHOTOVIEWERV3_SMOKE_CLEANUP";

    public static async Task RunIfRequestedAsync(IServiceProvider services, INavigationService navigation, ILogger logger)
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var cleanup = Environment.GetEnvironmentVariable(CleanupVariable) == "1";
        logger.LogInformation("Rauchtest: Start mit Root={Root} Cleanup={Cleanup}", root, cleanup);
        try
        {
            var libraryViewModel = services.GetRequiredService<LibraryViewModel>();

            // 1) Anlegen + Fresh-Scan (wechselt nach Erfolg automatisch zur Galerie)
            navigation.NavigateTo(NavigationTags.Library);
            await libraryViewModel.RunSmokeScanAsync(root).ConfigureAwait(true);
            await Task.Delay(1500).ConfigureAwait(true);
            logger.LogInformation("Rauchtest: nach Fresh-Scan auf Seite {Tag}", navigation.CurrentTag);

            // 2) Zweiter Scan = ClearAndRescan (Katalog leeren, FTS-Delete, fortlaufende Ids)
            navigation.NavigateTo(NavigationTags.Library);
            await libraryViewModel.RunSmokeScanAsync(root).ConfigureAwait(true);
            await Task.Delay(1500).ConfigureAwait(true);

            // 3) Ordnerbaum und Einstellungen anzeigen
            navigation.NavigateTo(NavigationTags.Folders);
            await Task.Delay(2000).ConfigureAwait(true);
            navigation.NavigateTo(NavigationTags.Settings);
            await Task.Delay(1500).ConfigureAwait(true);

            // 4) Optional aufräumen
            if (cleanup)
            {
                navigation.NavigateTo(NavigationTags.Library);
                await libraryViewModel.RunSmokeCleanupAsync(root).ConfigureAwait(true);
                await Task.Delay(1000).ConfigureAwait(true);
            }

            logger.LogInformation("Rauchtest: abgeschlossen (Seite={Tag})", navigation.CurrentTag);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rauchtest fehlgeschlagen");
        }
    }
}
