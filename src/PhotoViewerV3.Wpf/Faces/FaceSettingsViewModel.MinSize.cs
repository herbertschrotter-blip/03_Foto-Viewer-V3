using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Faces.Assignment;

namespace PhotoViewerV3.Faces;

/// <summary>Eintrag der Auswahl „Kleine Gesichter automatisch ignorieren“.</summary>
public sealed record FaceMinSizeChoice(int Value, string Label);

/// <summary>
/// Einstellung „Gesichter kleiner als … Pixel automatisch ignorieren“ (ADR-0040, Standard 24 px). Gespeichert und auf
/// den Bestand angewandt vom Zuordnungsdienst (<see cref="IFaceIgnoreService"/>, im Hintergrund); neue Gesichter
/// ignoriert der Index gleich beim Einfügen.
/// </summary>
public sealed partial class FaceSettingsViewModel
{
    private readonly IFaceIgnoreService? _ignore;
    private bool _loadingMinSize;

    /// <summary>Auswahl: Aus, 16 … 64 Pixel (Standard markiert).</summary>
    public IReadOnlyList<FaceMinSizeChoice> MinFaceSizeChoices { get; } = FaceAutoIgnore.Choices
        .Select(v => new FaceMinSizeChoice(v, v == 0
            ? "Aus"
            : $"kleiner als {v} Pixel" + (v == FaceAutoIgnore.DefaultMinSize ? " (Standard)" : string.Empty)))
        .ToList();

    /// <summary>Abschnitt sichtbar (Dienst registriert).</summary>
    public bool HasMinFaceSize => _ignore is not null;

    [ObservableProperty] private int _selectedMinFaceSize = FaceAutoIgnore.DefaultMinSize;

    [ObservableProperty] private string? _minFaceSizeMessage;

    /// <summary>Gespeicherte Schwelle lesen (beim Öffnen der Einstellungen).</summary>
    internal async Task LoadMinFaceSizeAsync()
    {
        if (_ignore is null)
        {
            return;
        }

        try
        {
            var value = await _ignore.GetMinFaceSizeAsync().ConfigureAwait(true);
            _loadingMinSize = true;
            try
            {
                SelectedMinFaceSize = value;
            }
            finally
            {
                _loadingMinSize = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gesichtserkennung: Mindestgröße nicht gelesen");
        }
    }

    partial void OnSelectedMinFaceSizeChanged(int value)
    {
        if (_loadingMinSize || _ignore is null)
        {
            return;
        }

        _ = SaveMinFaceSizeAsync(value);
    }

    /// <summary>Speichern; angewandt wird im Hintergrund (Tests warten darauf).</summary>
    internal async Task SaveMinFaceSizeAsync(int value)
    {
        try
        {
            await _ignore!.SetMinFaceSizeAsync(value).ConfigureAwait(true);
            MinFaceSizeMessage = value == 0
                ? "Ausgeschaltet — automatisch ignorierte Gesichter werden im Hintergrund zurückgeholt."
                : $"Gesichter kleiner als {value} Pixel werden im Hintergrund ignoriert; zu sehen unter Personen → Ignorierte Gesichter (markiert „zu klein“).";
        }
        catch (Exception ex)
        {
            MinFaceSizeMessage = $"Mindestgröße konnte nicht gespeichert werden: {ex.Message}";
            _logger.LogError(ex, "Gesichtserkennung: Mindestgröße nicht gespeichert");
            await LoadMinFaceSizeAsync().ConfigureAwait(true);
        }
    }
}
