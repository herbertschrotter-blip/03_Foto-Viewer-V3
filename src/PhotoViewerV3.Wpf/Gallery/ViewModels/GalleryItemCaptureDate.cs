using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Services.Metadata;
using CaptureText = PhotoViewerV3.Services.Metadata.CaptureDateText;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>
/// Aufnahmedatum der Kachel (ADR-0046): Kachel-Tooltip „Aufgenommen: 12.07.2024 15:30 (EXIF)“, Listenspalte
/// „Aufgenommen“ samt Sortierschlüssel innerhalb der Seite. Wird beim Neuladen der Seite in place ersetzt.
/// </summary>
public sealed partial class GalleryItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureDateText), nameof(CaptureDateToolTip), nameof(CaptureSortKey))]
    private CaptureDateInfo? _captureDate;

    /// <summary>Spalte „Aufgenommen“: „12.07.2024 15:30“, „ca. 1982“ bzw. leer ohne Datum.</summary>
    public string CaptureDateText => CaptureDate?.Value is { } value ? FormatCapture(value) : string.Empty;

    /// <summary>Kachel-Tooltip mit Herkunft; ohne Datum der Hinweis auf das Änderungsdatum.</summary>
    public string CaptureDateToolTip => "Aufgenommen: " + (CaptureDate?.Text ?? FormatCapture(null));

    /// <summary>Sortierschlüssel der Spalte (Unix-Sekunden; ohne Datum das Änderungsdatum wie die Galerie-Sortierung).</summary>
    public long CaptureSortKey => CaptureDate?.Value is { } value ? new DateTimeOffset(value.Utc).ToUnixTimeSeconds() : ModifiedAtUnix;

    private static string FormatCapture(CaptureDateValue? value)
        => value is { } v ? CaptureText.FormatDate(v) : CaptureText.Describe(null, null);
}
