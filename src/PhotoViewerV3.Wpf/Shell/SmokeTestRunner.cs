using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Services;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Shell.Smoke;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Rauchtest-Hook für unbeaufsichtigte Läufe (ohne Umgebungsvariable passiert nichts). Steuert die ViewModels
/// direkt aus DI — dieselben Pfade wie Klicks, ohne Maus/Tastatur — und prüft Dateisystem und Katalog-DB.
/// Umgebungsvariablen:
/// <list type="bullet">
/// <item>PHOTOVIEWERV3_SMOKE_ROOT = Ordner der Testbibliothek (Pflicht; Dateien darin werden verschoben/gelöscht!).</item>
/// <item>PHOTOVIEWERV3_SMOKE_SHOTS = Ordner für PNG-Screenshots der Hauptansichten (optional).</item>
/// <item>PHOTOVIEWERV3_SMOKE_EXIT = 1 → App nach dem Lauf beenden (ExitCode 0 = alle Schritte OK, 2 = Fehler).</item>
/// <item>PHOTOVIEWERV3_SMOKE_CLEANUP = 1 → die Bibliothek am Ende wieder aus dem Katalog entfernen.</item>
/// </list>
/// Zusammen mit PHOTOVIEWERV3_APPDATA (eigener AppData-Root) läuft der Test gegen eine frische DB.
/// Ergebnis: Logzeilen „Rauchtest …“ und die Datei <see cref="ResultFileName"/> im AppData-Root.
/// </summary>
public static class SmokeTestRunner
{
    public const string RootVariable = "PHOTOVIEWERV3_SMOKE_ROOT";
    public const string CleanupVariable = "PHOTOVIEWERV3_SMOKE_CLEANUP";
    public const string ShotsVariable = "PHOTOVIEWERV3_SMOKE_SHOTS";
    public const string ExitVariable = "PHOTOVIEWERV3_SMOKE_EXIT";
    public const string ResultFileName = "smoke-result.txt";

    private static int s_started;

    public static bool IsRequested => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootVariable));

    /// <summary>
    /// Registriert die Rauchtest-Fakes (Dialoge automatisch „Ja“, Ordnerauswahl aus dem Szenario) — nur wenn
    /// der Rauchtest angefordert ist. Muss vor <c>AddGallery()</c> laufen, das per TryAdd die echten Dienste setzt.
    /// </summary>
    public static IServiceCollection ConfigureServices(IServiceCollection services)
    {
        if (!IsRequested)
        {
            return services;
        }

        services.AddSingleton<SmokeFolderPickerService>();
        services.AddSingleton<IFolderPickerService>(sp => sp.GetRequiredService<SmokeFolderPickerService>());
        services.AddSingleton<SmokeGalleryDialogService>();
        services.AddSingleton<IGalleryDialogService>(sp => sp.GetRequiredService<SmokeGalleryDialogService>());
        services.AddSingleton<SmokeArchiveTargetPicker>();
        services.AddSingleton<PhotoViewerV3.Gallery.ArchiveTarget.IArchiveTargetPicker>(sp => sp.GetRequiredService<SmokeArchiveTargetPicker>());
        services.AddSingleton<SmokeTagPickerService>();
        services.AddSingleton<Gallery.Tags.ITagPickerService>(sp => sp.GetRequiredService<SmokeTagPickerService>());
        services.AddSingleton<SmokeCategoryPickerService>();
        services.AddSingleton<Gallery.Categories.ICategoryPickerService>(sp => sp.GetRequiredService<SmokeCategoryPickerService>());
        services.AddSingleton<SmokeFolderAdoptionPicker>(); // „Ordner in die Bibliothek übernehmen…“ (ADR-0034)
        services.AddSingleton<Folders.Adoption.IFolderAdoptionPicker>(sp => sp.GetRequiredService<SmokeFolderAdoptionPicker>());
        // Gesichtserkennung ohne Modelle (ADR-0035): Replace, weil AddPhotoViewerServices schon registriert hat
        services.Replace(ServiceDescriptor.Singleton<Services.Faces.IFaceAnalyzerFactory, SmokeFaceAnalyzerFactory>());
        // Personen-Seite (ADR-0037): Dialoge mit vorgegebenen Antworten, vor AddPersonsUi (TryAdd)
        services.AddSingleton<SmokePersonsDialogs>();
        services.AddSingleton<Persons.IPersonsDialogService>(sp => sp.GetRequiredService<SmokePersonsDialogs>());
        // „Datum setzen…“ (ADR-0046): vorgegebene Antwort, vor AddCaptureDateUi (TryAdd)
        services.AddSingleton<Smoke.SmokeCaptureDateDialog>();
        services.AddSingleton<Gallery.CaptureDates.ICaptureDateDialogService>(sp => sp.GetRequiredService<Smoke.SmokeCaptureDateDialog>());
        // Seite „Orte“ (ADR-0051): „Fotos verorten“ mit vorgegebener Antwort, vor AddPlacesMap (TryAdd); Kachelquelle schaltet
        // MapTileOptions.FromEnvironment im Rauchtest selbst ab (kein Netz)
        services.AddSingleton<Smoke.SmokePlacesDialogs>();
        services.AddSingleton<Places.Dialogs.IPlacesDialogs>(sp => sp.GetRequiredService<Smoke.SmokePlacesDialogs>());
        // „Ort setzen…“ (ADR-0050): vorgegebene Antwort, vor AddPlacesUi (TryAdd)
        services.AddSingleton<Smoke.SmokePlaceDialog>();
        services.AddSingleton<Gallery.Places.IPlaceDialogService>(sp => sp.GetRequiredService<Smoke.SmokePlaceDialog>());
        return services;
    }

    public static async Task RunIfRequestedAsync(IServiceProvider services, INavigationService navigation, ILogger logger)
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        // Ein Lauf je Prozess: nach einem Katalogwechsel (versteckte Bibliothek, ADR-0049) lädt ein neues Hauptfenster
        if (System.Threading.Interlocked.Exchange(ref s_started, 1) != 0)
        {
            return;
        }

        var cleanup = Environment.GetEnvironmentVariable(CleanupVariable) == "1";
        var exit = Environment.GetEnvironmentVariable(ExitVariable) == "1";
        var shots = Environment.GetEnvironmentVariable(ShotsVariable);
        shots = string.IsNullOrWhiteSpace(shots) ? null : Path.GetFullPath(shots);
        var paths = services.GetRequiredService<IAppDataPaths>();
        logger.LogInformation(
            "Rauchtest: Start Root={Root} Screenshots={Shots} Cleanup={Cleanup} Exit={Exit}",
            root, shots ?? "-", cleanup, exit);

        var failed = 0;
        var lines = new List<string>();
        try
        {
            var scenario = new SmokeScenario(services, navigation, logger, Path.GetFullPath(root), shots, cleanup);
            await scenario.RunAsync().ConfigureAwait(true);
            failed = scenario.Failed;
            lines.AddRange(scenario.Results);
        }
        catch (Exception ex)
        {
            failed++;
            lines.Add($"FAIL Rauchtest-Rahmen: {ex.Message}");
            logger.LogError(ex, "Rauchtest fehlgeschlagen");
        }

        var summary = $"SMOKE RESULT: {lines.Count - failed} OK, {failed} FAIL, WPF-Binding-Fehler: {BindingErrorTraceListener.ErrorCount}";
        lines.Add(summary);
        logger.LogInformation("Rauchtest: {Summary}", summary);
        foreach (var line in lines)
        {
            logger.LogInformation("Rauchtest: {Line}", line);
        }

        try
        {
            File.WriteAllLines(Path.Combine(paths.AppDataRoot, ResultFileName), lines);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rauchtest: Ergebnisdatei konnte nicht geschrieben werden");
        }

        if (exit)
        {
            var code = failed == 0 ? 0 : 2;
            logger.LogInformation("Rauchtest: App wird beendet (ExitCode {Code})", code);
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Application.Current.Shutdown(code));
        }
    }
}
