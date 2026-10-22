using System.Collections.Generic;
using Microsoft.Win32;

namespace Lumora.Places.TrackSettings;

/// <summary>Dateiauswahl für GPS-Spuren (austauschbar für Tests und Rauchtest).</summary>
public interface IGpsTrackFilePicker
{
    /// <summary>Garmin-Datenexport (ZIP); null bei Abbruch.</summary>
    string? PickExportZip();

    /// <summary>Einzelne FIT-/GPX-/TCX-Dateien; leer bei Abbruch.</summary>
    IReadOnlyList<string> PickTrackFiles();
}

/// <summary>OpenFileDialog ohne Eintrag unter „Zuletzt verwendet“ (ADR-0049: keine Spuren außerhalb).</summary>
public sealed class GpsTrackFilePicker : IGpsTrackFilePicker
{
    public string? PickExportZip()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Garmin-Datenexport (ZIP) einlesen",
            Filter = "Garmin-Datenexport (*.zip)|*.zip|Alle Dateien (*.*)|*.*",
            AddToRecent = false,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickTrackFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "GPS-Spuren einlesen (FIT, GPX, TCX)",
            Filter = "GPS-Spuren (*.fit;*.gpx;*.tcx;*.zip)|*.fit;*.gpx;*.tcx;*.zip|Alle Dateien (*.*)|*.*",
            AddToRecent = false,
            Multiselect = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}
