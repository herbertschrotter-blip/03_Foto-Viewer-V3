namespace PhotoViewerV3.Sorter.Services;

/// <summary>Datei-Dialoge für Import/Export der Pattern-Profile (Implementierung: Microsoft.Win32-Dialoge).</summary>
public interface ISorterFileDialogService
{
    /// <summary>Gibt den gewählten Dateipfad zurück oder null, wenn der Benutzer abgebrochen hat.</summary>
    string? PickOpenFile(string title, string filter, string? initialDirectory);

    string? PickSaveFile(string title, string filter, string defaultFileName, string? initialDirectory);
}
