using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Services.Messaging;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Hinweis „Änderung wartet auf den Katalog…“ (0.2.29, ADR-0030): solange Schreibvorgänge (Bewertung, Tags, Kategorien,
/// Einstellungen, …) auf die Schreibsperre der Katalog-DB warten — typischerweise während „Neu aufbauen“ den Katalog
/// schreibt. Die Änderung ist nicht verloren; sie wird nach dem Commit automatisch gespeichert.
/// </summary>
public sealed partial class StatusBarViewModel
{
    /// <summary>Text des Hinweises; null = niemand wartet.</summary>
    [ObservableProperty] private string? _catalogWriteWaitText;

    private void InitCatalogWriteWait()
        => _messenger.Register<StatusBarViewModel, CatalogWriteWaitMessage>(this, static (r, m) => UiThread.Post(() => r.OnCatalogWriteWait(m)));

    private void OnCatalogWriteWait(CatalogWriteWaitMessage message)
    {
        CatalogWriteWaitText = message.WaitingWriters switch
        {
            <= 0 => null,
            1 => "Änderung wartet auf den Katalog…",
            var n => $"{n} Änderungen warten auf den Katalog…",
        };
    }
}
