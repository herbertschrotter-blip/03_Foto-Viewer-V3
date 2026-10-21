using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Services.HiddenLibraries;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Shell.HiddenLibraries;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Versteckte Bibliothek“ (ADR-0049). Der Startschalter <c>_hidden</c> ist im Rauchtest simuliert
/// (<see cref="HiddenModeSwitch.SmokeVariable"/>), der „Container“ ist ein Temp-Ordner neben der Testbibliothek und das
/// Aushängen ein Fake (<see cref="SmokeContainerTools"/>). Ablauf: anlegen → eigene Sitzung mit Galerie, Vorschaubildern
/// und Protokoll im Container → Zusperren nach Untätigkeit (Aushängen angestoßen) → falsches Kennwort abgewiesen →
/// wieder öffnen (Katalog bleibt) → zusperren → normale Bibliothek unverändert → Spurenprüfung im AppData-Testordner.
/// Danach ist die normale Sitzung neu aufgebaut: die restlichen Schritte laufen in einem Nachfolger mit den neuen Diensten.
/// Protokollzeilen nennen weder Namen noch Pfade der versteckten Bibliothek (sie landen im normalen Protokoll).
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string HiddenSmokePassword = "smoke-geheim-42";

    private ICatalogSessionManager? _hiddenSessions;

    /// <summary>Szenario mit den Diensten der neu aufgebauten normalen Sitzung (nach dem Zusperren).</summary>
    private SmokeScenario? _successor;

    private void InitHiddenLibrary(IServiceProvider services)
        => _hiddenSessions = services.GetService<ICatalogSessionManager>();

    private async Task HiddenLibraryAsync()
    {
        var sessions = _hiddenSessions ?? throw new SmokeCheckException("Sitzungsverwalter fehlt");
        var switcher = Application.Current as ICatalogHostSwitcher ?? throw new SmokeCheckException("App ist kein Katalog-Wechsler");
        var containers = sessions.Containers as SmokeContainerTools ?? throw new SmokeCheckException("Container-Fake fehlt");
        Check(sessions.IsHiddenModeEnabled, "_hidden simuliert (PHOTOVIEWERV3_SMOKE_HIDDEN)");

        // Normale Bibliothek vorher
        var librariesBefore = await DbScalarAsync<int>("SELECT COUNT(*) FROM Libraries").ConfigureAwait(true);
        var filesBefore = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        var appDataRoot = _paths.AppDataRoot;

        // „Container“ = Ordner neben der Testbibliothek (nie darin), eindeutige Namen für die Spurenprüfung
        var token = Guid.NewGuid().ToString("N")[..10];
        var container = Path.Combine(Path.GetDirectoryName(_root) ?? _root, Path.GetFileName(_root) + "-Container");
        if (Directory.Exists(container))
        {
            Directory.Delete(container, recursive: true);
        }

        var libraryFolder = Path.Combine(container, "Tresor" + token);
        Directory.CreateDirectory(libraryFolder);
        var name = "Versteckt" + token;
        var sources = Directory.EnumerateFiles(_root, "*.jpg", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Contains("smoke", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        Check(sources.Count == 3, "drei Testfotos für den Container");
        var fileNames = new List<string>();
        for (var i = 0; i < sources.Count; i++)
        {
            var fileName = $"geheim{token}-{i + 1}.jpg";
            File.Copy(sources[i], Path.Combine(libraryFolder, fileName));
            fileNames.Add(fileName);
        }

        try
        {
            // (1) Anlegen → eigene Sitzung
            var created = await sessions.CreateAsync(libraryFolder, name, HiddenSmokePassword).ConfigureAwait(true);
            Check(created.Success, $"versteckte Bibliothek angelegt ({created.Message ?? "ok"})");
            Check(sessions.IsHiddenSessionActive && HiddenSessionPrivacy.IsHiddenSessionActive, "versteckte Sitzung aktiv, Explorer-Aufrufe gesperrt");
            var hidden = switcher.CurrentServices;
            Check(hidden.GetRequiredService<ICatalogContext>().IsHidden, "Katalog-Kontext der Sitzung: versteckt");
            if (sessions.InitialScan is { } scan)
            {
                await scan.WaitAsync(LongWait).ConfigureAwait(true);
            }

            var gallery = hidden.GetRequiredService<GalleryViewModel>();
            await WaitUntilAsync(() => !gallery.IsLoading && gallery.TotalCount == 3, LongWait, "Galerie der versteckten Bibliothek zeigt 3 Fotos").ConfigureAwait(true);
            var catalog = HiddenLibraryStore.GetCatalogFolder(libraryFolder);
            Check(File.Exists(Path.Combine(catalog, AppDataPaths.DatabaseFileName)), "Katalog-DB im Container (.lumora)");
            await WaitUntilAsync(
                () => Directory.Exists(Path.Combine(catalog, AppDataPaths.ThumbnailsFolderName))
                      && Directory.EnumerateFiles(Path.Combine(catalog, AppDataPaths.ThumbnailsFolderName), "*.jpg", SearchOption.AllDirectories).Any(),
                LongWait,
                "Vorschaubilder im Container").ConfigureAwait(true);
            Check(Directory.EnumerateFiles(Path.Combine(catalog, "logs"), CatalogLoggers.HiddenFilePattern.Replace("-.log", "-*.log")).Any(), "Protokoll im Container");
            Check((new DirectoryInfo(catalog).Attributes & FileAttributes.Hidden) != 0, ".lumora ist versteckt");

            // (2) Untätigkeit → automatisch zugesperrt, Aushängen angestoßen
            sessions.Idle.Timeout = TimeSpan.Zero;
            var locked = await sessions.CheckIdleAsync().ConfigureAwait(true);
            Check(locked && !sessions.IsHiddenSessionActive && !HiddenSessionPrivacy.IsHiddenSessionActive, "nach Untätigkeit zugesperrt");
            Check(containers.Dismounted.Count == 1, "Aushängen beim Zusperren angestoßen (Fake)");
            Check(!switcher.CurrentServices.GetRequiredService<ICatalogContext>().IsHidden, "wieder im normalen Katalog");

            // (3) Falsches Kennwort
            var info = new HiddenLibraryInfo(libraryFolder, name);
            var wrong = await sessions.OpenAsync(info, "falsch-123").ConfigureAwait(true);
            Check(!wrong.Success && !sessions.IsHiddenSessionActive, "falsches Kennwort abgewiesen");

            // (4) Wieder öffnen: vorhandener Katalog, kein neuer Scan
            var opened = await sessions.OpenAsync(info, HiddenSmokePassword).ConfigureAwait(true);
            Check(opened.Success && sessions.IsHiddenSessionActive && sessions.InitialScan is null, "wieder geöffnet (Katalog bleibt, kein neuer Scan)");
            var reopened = switcher.CurrentServices.GetRequiredService<GalleryViewModel>();
            await WaitUntilAsync(() => !reopened.IsLoading && reopened.TotalCount == 3, LongWait, "Galerie nach erneutem Öffnen").ConfigureAwait(true);

            // (5) Zusperren von Hand
            var manual = await sessions.LockAsync().ConfigureAwait(true);
            Check(manual.Success && !sessions.IsHiddenSessionActive && containers.Dismounted.Count == 2, "von Hand zugesperrt, Aushängen angestoßen");
        }
        finally
        {
            if (sessions.IsHiddenSessionActive)
            {
                await sessions.LockAsync().ConfigureAwait(true);
            }
        }

        // (6) Normale Sitzung neu aufgebaut: Nachfolger mit den neuen Diensten, normale Bibliothek unverändert
        var services = switcher.CurrentServices;
        var navigation = services.GetRequiredService<INavigationService>();
        await WaitUntilAsync(() => navigation.CurrentTag is not null, LongWait, "normale Oberfläche wieder aufgebaut").ConfigureAwait(true);
        var next = new SmokeScenario(services, navigation, _logger, _root, _shotsDir, _cleanup)
        {
            _libraryId = _libraryId,
            _mediaCount = _mediaCount,
        };
        var librariesAfter = await next.DbScalarAsync<int>("SELECT COUNT(*) FROM Libraries").ConfigureAwait(true);
        var filesAfter = await next.DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(librariesAfter == librariesBefore && filesAfter == filesBefore, $"normale Bibliothek unverändert ({librariesAfter} Bibliothek(en), {filesAfter} Dateien)");
        _successor = next;

        // (7) Spurenprüfung: Name, Pfad und Dateinamen kommen im AppData-Testordner nirgends vor
        var needles = new List<string> { name, "Tresor" + token, libraryFolder, token };
        needles.AddRange(fileNames);
        var hits = FindTraces(appDataRoot, needles);
        Check(hits.Count == 0, $"keine Spuren im AppData-Testordner ({hits.Count} Treffer{(hits.Count > 0 ? ": " + string.Join(", ", hits.Take(5)) : string.Empty)})");

        try
        {
            Directory.Delete(container, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Rauchtest   Container-Ordner bleibt liegen ({Type})", ex.GetType().Name);
        }
    }

    /// <summary>Durchsucht alle Dateien (auch offene DB/WAL/Protokolle) nach den Texten in UTF-8 und UTF-16; liefert Dateinamen der Treffer.</summary>
    internal static List<string> FindTraces(string root, IReadOnlyList<string> needles)
    {
        var patterns = needles
            .Where(n => !string.IsNullOrEmpty(n))
            .SelectMany(n => new[] { Encoding.UTF8.GetBytes(n), Encoding.Unicode.GetBytes(n) })
            .ToList();
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (needles.Any(n => Path.GetFileName(file).Contains(n, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(Path.GetFileName(file));
                continue;
            }

            byte[] content;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                content = memory.ToArray();
            }
            catch (IOException)
            {
                hits.Add(Path.GetFileName(file) + " (nicht lesbar)");
                continue;
            }

            if (patterns.Any(p => content.AsSpan().IndexOf(p) >= 0))
            {
                hits.Add(Path.GetFileName(file));
            }
        }

        return hits;
    }

    /// <summary>Restliche Schritte nach dem Katalogwechsel — im Nachfolger, falls die normale Sitzung neu aufgebaut wurde.</summary>
    private async Task RunAfterHiddenLibraryAsync(Func<SmokeScenario, Task> remaining)
    {
        if (_successor is not { } next)
        {
            await remaining(this).ConfigureAwait(true);
            return;
        }

        try
        {
            await remaining(next).ConfigureAwait(true);
        }
        finally
        {
            _results.AddRange(next._results);
            Failed += next.Failed;
        }
    }

    /// <summary>Ordnerüberwachung der aktiven Sitzung (nach dem Wechsel die des Nachfolgers).</summary>
    private Services.Watch.ILibraryWatchService CurrentWatch => (_successor ?? this)._watch;
}

