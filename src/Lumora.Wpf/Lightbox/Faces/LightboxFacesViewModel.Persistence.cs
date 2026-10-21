using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.ListView;

namespace Lumora.Lightbox.Faces;

/// <summary>
/// Gesichtsrahmen an/aus (Taste F) dauerhaft merken (0.2.39): Schlüssel <see cref="ShowFacesKey"/> in der
/// Settings-Tabelle (Key-Value-Zugriff über <see cref="IGalleryViewSettingsStore"/>, wie Ansicht und Vorschaugröße der
/// Galerie). Jede Taste F schreibt den neuen Zustand; ein neues Lightbox-Fenster stellt ihn mit
/// <see cref="RestoreAsync"/> wieder her. Beim Blättern bleibt <see cref="IsVisible"/> ohnehin stehen.
/// </summary>
public sealed partial class LightboxFacesViewModel
{
    /// <summary>Settings-Schlüssel: „true“, wenn die Gesichtsrahmen beim Öffnen der Lightbox eingeschaltet sein sollen.</summary>
    public const string ShowFacesKey = "ui.lightbox.showFaces";

    private readonly IGalleryViewSettingsStore? _settings;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _toggledByUser;

    /// <summary>
    /// Gemerkten Zustand laden und anwenden (ohne Statusmeldung). Hat der Benutzer in der Zwischenzeit schon F gedrückt,
    /// gilt seine Taste. Ohne Zuordnungsdienst oder Settings-Zugriff passiert nichts; Fehler werden nur protokolliert.
    /// </summary>
    public async Task RestoreAsync()
    {
        if (_service is null || _settings is null)
        {
            return;
        }

        try
        {
            var value = await _settings.GetAsync(ShowFacesKey).ConfigureAwait(true);
            if (_disposed || _toggledByUser || !IsTrue(value) || IsVisible)
            {
                return;
            }

            IsVisible = true;
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gesichtsrahmen: gemerkter Zustand konnte nicht geladen werden");
        }
    }

    private async Task SaveVisibilityAsync()
    {
        if (_settings is null)
        {
            return;
        }

        // Nacheinander schreiben und den Zustand erst im Schloss lesen: bei schnellem F-F gewinnt immer der letzte Stand.
        await _saveLock.WaitAsync().ConfigureAwait(true);
        try
        {
            await _settings.SetAsync(ShowFacesKey, IsVisible ? "true" : "false").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gesichtsrahmen: Zustand konnte nicht gespeichert werden");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static bool IsTrue(string? value)
        => bool.TryParse(value, out var flag) ? flag : value == "1";
}
