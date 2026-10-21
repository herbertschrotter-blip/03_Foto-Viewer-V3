using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Motifs;

namespace Lumora.Lightbox;

/// <summary>
/// Motive in der Lightbox (ADR-0052): Info-Panel (I) „Motive: Strand, Meer, Sonnenuntergang“ (wahrscheinlichste zuerst).
/// Ohne <see cref="IMotifTermService"/> (Tests) bleibt der Abschnitt verborgen.
/// </summary>
public sealed partial class LightboxViewModel
{
    private IMotifTermService? _motifs;
    private int _motifsVersion;

    /// <summary>Motive der aktuellen Datei, leer ohne Treffer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentMotifs))]
    private string _currentMotifsText = string.Empty;

    public bool HasCurrentMotifs => CurrentMotifsText.Length > 0;

    private void InitMotifs(IMotifTermService? motifs)
    {
        _motifs = motifs;
        if (motifs is null)
        {
            return;
        }

        WeakReferenceMessenger.Default.Register<MotifsChangedMessage>(
            this,
            static (recipient, message) =>
            {
                var vm = (LightboxViewModel)recipient;
                if (vm.CurrentItem is { } item && (message.FileIds.Count == 0 || message.FileIds.Contains(item.FileId)))
                {
                    vm.RunOnUi(() => _ = vm.LoadCurrentMotifsAsync());
                }
            });
    }

    /// <summary>Motive der aktuellen Datei laden; ältere Ladevorgänge (schnelles Blättern) werden verworfen.</summary>
    private async Task LoadCurrentMotifsAsync()
    {
        var item = CurrentItem;
        var version = ++_motifsVersion;
        if (item is null || _motifs is null)
        {
            CurrentMotifsText = string.Empty;
            return;
        }

        try
        {
            var rows = await _motifs.GetMotifsForFilesAsync([item.FileId]).ConfigureAwait(true);
            if (_disposed || version != _motifsVersion)
            {
                return;
            }

            CurrentMotifsText = string.Join(", ", rows.Select(r => r.Name));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Motive für File {FileId} konnten nicht geladen werden", item.FileId);
        }
    }
}
