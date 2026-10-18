namespace PhotoViewerV3.Gallery.Services;

/// <summary>Ordnerauswahl für „Verschieben nach…“ (Implementierung: Microsoft.Win32.OpenFolderDialog).</summary>
public interface IFolderPickerService
{
    /// <summary>Gibt den gewählten Ordner zurück oder null, wenn der Benutzer abgebrochen hat.</summary>
    string? PickFolder(string title, string? initialDirectory);
}
