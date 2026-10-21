using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Faces.Indexing;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Gesichter“ (0.2.34, ADR-0035) — ohne echte Modelle: der Analysator ist
/// <see cref="SmokeFaceAnalyzerFactory"/> (ein Gesicht je Bild, 150 ms), Bilder kommen über den echten WIC-Lader.
/// Einschalten über den Einstellungs-Abschnitt → Index läuft (Statusleiste „Gesichter: n / m“), Pause über die
/// Statusleiste hält an (keine neuen FaceScans), Fortsetzen → alle Fotos haben FaceScans und Faces (Fingerabdruck
/// 2048 Bytes), Statusleiste leer; danach wieder ausschalten.
/// </summary>
internal sealed partial class SmokeScenario
{
    private IFaceIndexService? _faceIndex;

    private async Task FacesAsync()
    {
        Check(_faceIndex is not null && _settings.Faces is not null, "Gesichts-Index und Einstellungs-Abschnitt vorhanden");
        var faces = _settings.Faces!;
        var index = _faceIndex!;

        _navigation.NavigateTo(NavigationTags.Settings);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Settings, ShortWait, "Einstellungen").ConfigureAwait(true);
        faces.Load();
        Check(!faces.IsEnabled && index.Status.State == FaceIndexState.Disabled, $"Gesichtserkennung standardmäßig aus ({index.Status.State})");

        var images = await DbScalarAsync<long>(
            "SELECT COUNT(*) FROM Files WHERE LibraryId = $lib AND MediaType = 0 AND Missing = 0", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(images >= 5, $"{images} Fotos in der Bibliothek");

        faces.IsEnabled = true; // wie der Schalter in den Einstellungen
        await WaitUntilAsync(
            () => _statusBar.FaceIndexText is { } t && t.StartsWith("Gesichter:", StringComparison.Ordinal) && index.Status.Done >= 2,
            LongWait,
            "Statusleiste zeigt den laufenden Gesichts-Index").ConfigureAwait(true);
        Check(_statusBar.FaceIndexText!.Contains(" / ", StringComparison.Ordinal), $"Statusleiste: „{_statusBar.FaceIndexText}“");
        var section = FindByAutomationName(Application.Current.MainWindow, "Gesichtserkennung", typeof(Border));
        Check(section is not null, "Einstellungs-Abschnitt „Gesichtserkennung“ sichtbar");
        section!.BringIntoView();
        await SnapshotAsync("21-einstellungen-und-statusleiste-gesichter").ConfigureAwait(true);

        // Pause über die Statusleiste: nach dem aktuellen Foto kommt nichts mehr dazu.
        Check(_statusBar.PauseFaceIndexCommand.CanExecute(null), "Pause ist bedienbar");
        _statusBar.PauseFaceIndexCommand.Execute(null);
        await WaitUntilAsync(() => index.Status.State == FaceIndexState.Paused && _statusBar.FaceIndexIsPaused, ShortWait, "Index pausiert").ConfigureAwait(true);
        await Task.Delay(SmokeFaceAnalyzerFactory.Delay * 3).ConfigureAwait(true);
        var pausedScans = await CountFaceScansAsync().ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(true);
        var stillPaused = await CountFaceScansAsync().ConfigureAwait(true);
        Check(stillPaused == pausedScans && pausedScans < images, $"pausiert: {pausedScans} → {stillPaused} FaceScans (von {images})");
        Check(_statusBar.FaceIndexText!.Contains("angehalten", StringComparison.Ordinal), $"Statusleiste: „{_statusBar.FaceIndexText}“");

        _statusBar.ResumeFaceIndexCommand.Execute(null);
        await WaitUntilAsync(() => index.Status.State == FaceIndexState.Idle && _statusBar.FaceIndexText is null, LongWait, "Index fertig").ConfigureAwait(true);

        var scans = await CountFaceScansAsync().ConfigureAwait(true);
        var faceRows = await DbScalarAsync<long>(
            "SELECT COUNT(*) FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE f.LibraryId = $lib AND x.ModelVersion = $ver AND length(x.Embedding) = $len AND length(x.Landmarks) = 40",
            ("$lib", _libraryId), ("$ver", SmokeFaceAnalyzerFactory.Version), ("$len", FaceEmbeddingCodec.ByteLength)).ConfigureAwait(true);
        var decodeErrors = await DbScalarAsync<long>(
            "SELECT COUNT(*) FROM FaceScans s JOIN Files f ON f.Id = s.FileId WHERE f.LibraryId = $lib AND s.ErrorCode IS NOT NULL", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(scans == images, $"FaceScans für alle {images} Fotos ({scans})");
        Check(faceRows == scans - decodeErrors && faceRows > 0, $"Faces: {faceRows} mit Fingerabdruck (2048 Bytes) und Landmarken, {decodeErrors} nicht dekodierbar");
        Check(index.Status.Done == images && index.Status.Total == images, $"Status {index.Status.Done}/{index.Status.Total}");

        faces.IsEnabled = false;
        await WaitUntilAsync(() => index.Status.State == FaceIndexState.Disabled, ShortWait, "Gesichtserkennung wieder aus").ConfigureAwait(true);
    }

    private static FrameworkElement? FindByAutomationName(DependencyObject? root, string name, Type type)
    {
        if (root is null)
        {
            return null;
        }

        if (root is FrameworkElement element && type.IsInstanceOfType(element)
            && AutomationProperties.GetName(element) == name && element.IsVisible)
        {
            return element;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindByAutomationName(VisualTreeHelper.GetChild(root, i), name, type) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private Task<long> CountFaceScansAsync()
        => DbScalarAsync<long>(
            "SELECT COUNT(*) FROM FaceScans s JOIN Files f ON f.Id = s.FileId WHERE f.LibraryId = $lib", ("$lib", _libraryId));
}
