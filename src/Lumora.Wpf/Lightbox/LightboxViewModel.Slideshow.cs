using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Lumora.Lightbox;

/// <summary>
/// „Diashow ab hier“ (ADR-0059, Taste S): dieselbe Sequenz wie die Lightbox, ab der aktuellen Datei. Ein laufendes Video
/// der Lightbox wird vorher gestoppt (kein doppelter Ton); die Lightbox bleibt offen und ist nach Esc wieder da.
/// </summary>
public sealed partial class LightboxViewModel
{
    private readonly Lumora.Slideshow.ISlideshowService? _slideshow;

    /// <summary>True, wenn die Diashow verfügbar ist (Knopf).</summary>
    public bool HasSlideshow => _slideshow is not null;

    [RelayCommand]
    private void StartSlideshow()
    {
        if (_slideshow is null || _items.Count == 0 || CurrentIndex < 0)
        {
            return;
        }

        StopVideo();
        var ids = _items.Select(i => i.FileId).ToList();
        _logger.LogInformation("Diashow aus der Lightbox: {Count} Dateien ab {Index}", ids.Count, CurrentIndex);
        _slideshow.Start(ids, CurrentIndex);
    }
}
