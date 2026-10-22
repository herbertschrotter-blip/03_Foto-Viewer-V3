using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Messaging;
using Lumora.Services.Places.Tracks;
using Lumora.Shell;

namespace Lumora.Places.TrackSettings;

/// <summary>
/// Garmin-Uhr per USB erkannt (ADR-0062): vor dem Einlesen fragen („n neue Aktivitäten auf der Uhr — jetzt einlesen?“),
/// danach im Hintergrund einlesen. Lebt so lange wie die Katalog-Sitzung (Start in <c>App.StartBackgroundServicesAsync</c>).
/// </summary>
public sealed class GarminDevicePrompt : IDisposable
{
    private readonly IGpsTrackAutoImport _autoImport;
    private readonly IDialogService _dialogs;
    private readonly ILogger<GarminDevicePrompt> _logger;
    private readonly IMessenger _messenger;
    private bool _asking;

    public GarminDevicePrompt(IGpsTrackAutoImport autoImport, IDialogService dialogs, ILogger<GarminDevicePrompt> logger, IMessenger? messenger = null)
    {
        _autoImport = autoImport;
        _dialogs = dialogs;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public void Start()
        => _messenger.Register<GarminDevicePrompt, GarminDeviceDetectedMessage>(this, static (r, m) => UiThread.Post(() => _ = r.AskAsync(m)));

    /// <summary>Mit der Katalog-Sitzung: abmelden, damit eine alte Sitzung nie mehr fragt.</summary>
    public void Dispose() => _messenger.UnregisterAll(this);

    private async Task AskAsync(GarminDeviceDetectedMessage message)
    {
        if (_asking)
        {
            return;
        }

        _asking = true;
        try
        {
            var count = message.NewFileCount;
            var ok = await _dialogs.ConfirmAsync(
                "Garmin-Uhr erkannt",
                $"Auf der Uhr {(count == 1 ? "ist 1 neue Aktivität" : $"sind {count} neue Aktivitäten")}. Jetzt einlesen? Die GPS-Spuren helfen beim Verbessern ungenauer Orte (Seite Orte › Ungenau). Auf der Uhr wird nichts verändert.",
                "Einlesen",
                cancelText: "Nicht jetzt").ConfigureAwait(true);
            if (ok)
            {
                var report = await Task.Run(() => _autoImport.ImportDeviceAsync(message.ActivityFolder)).ConfigureAwait(true);
                _logger.LogInformation("Garmin-Uhr eingelesen: {Imported} neu, {Duplicates} schon vorhanden", report.Imported, report.Duplicates);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Garmin-Uhr nicht eingelesen");
        }
        finally
        {
            _asking = false;
        }
    }
}
