using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Lumora.Gallery.ListView;
using Lumora.Lightbox.Faces;
using Lumora.Services.Faces.Assignment;
using Lumora.Shell;

namespace Lumora.Lightbox;

/// <summary>
/// Gesichter in der Lightbox (0.2.38): Umschalter „Gesichter“ (Taste F) und Rahmen über dem Bild. Die Logik liegt in
/// <see cref="LightboxFacesViewModel"/> (ohne LibVLC testbar); hier nur die Anbindung an Navigation und Status.
/// Ohne <see cref="IFaceRecognitionService"/> (Tests) bleibt der Umschalter wirkungslos.
/// </summary>
public sealed partial class LightboxViewModel
{
    /// <summary>Gesichtsrahmen des aktuellen Fotos (Fenster bindet <c>Faces.Boxes</c> und <c>Faces.ShowBoxes</c>).</summary>
    public LightboxFacesViewModel Faces { get; private set; } = null!;

    /// <param name="settings">Seit 0.2.39: Taste F wird in <c>ui.lightbox.showFaces</c> gemerkt und hier wiederhergestellt.</param>
    private void InitFaces(IFaceRecognitionService? faceRecognition, IDialogService? dialogs, IGalleryViewSettingsStore? settings)
    {
        Faces = new LightboxFacesViewModel(faceRecognition, dialogs, _logger, settings: settings) { ShowStatus = ShowStatus };
        _ = Faces.RestoreAsync(); // fängt Fehler selbst ab
    }

    /// <summary>Beim Blättern: Rahmen der neuen Datei (nur Fotos) laden, wenn eingeschaltet.</summary>
    private void UpdateFacesForCurrent()
    {
        var item = CurrentItem;
        _ = Faces.SetFileAsync(item?.FileId, item is { IsVideo: false, IsArchive: false });
    }

    /// <summary>Taste F: Gesichtsrahmen ein-/ausblenden.</summary>
    [RelayCommand]
    private Task ToggleFacesAsync() => Faces.ToggleAsync();
}
