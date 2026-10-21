using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PhotoViewerV3.Lightbox.Faces;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Lightbox;

/// <summary>
/// Gesichter in der Lightbox (0.2.38): Umschalter „Gesichter“ (Taste F) und Rahmen über dem Bild. Die Logik liegt in
/// <see cref="LightboxFacesViewModel"/> (ohne LibVLC testbar); hier nur die Anbindung an Navigation und Status.
/// Ohne <see cref="IFaceRecognitionService"/> (Tests) bleibt der Umschalter wirkungslos.
/// </summary>
public sealed partial class LightboxViewModel
{
    /// <summary>Gesichtsrahmen des aktuellen Fotos (Fenster bindet <c>Faces.Boxes</c> und <c>Faces.ShowBoxes</c>).</summary>
    public LightboxFacesViewModel Faces { get; private set; } = null!;

    private void InitFaces(IFaceRecognitionService? faceRecognition, IDialogService? dialogs)
    {
        Faces = new LightboxFacesViewModel(faceRecognition, dialogs, _logger) { ShowStatus = ShowStatus };
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
