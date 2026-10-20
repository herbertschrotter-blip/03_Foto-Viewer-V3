using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using PhotoViewerV3.Gallery.Controls;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Katalog gesperrt“ (0.2.29, ADR-0030): der Schritt hält die Schreibsperre der Katalog-DB selbst —
/// wie die eine Schreibtransaktion von „Neu aufbauen“ — länger als busy_timeout und setzt währenddessen über die Kachel
/// eine Bewertung. Erwartet: der Aufruf kehrt sofort zurück (UI-Thread wartet nicht auf SQLite), die Statusleiste zeigt
/// „Änderung wartet auf den Katalog…“, nach der Freigabe steht die Bewertung ohne Fehlermeldung in der DB.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task CatalogWriteWaitAsync()
    {
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie").ConfigureAwait(true);
        await WaitForGalleryAsync("Galerie geladen").ConfigureAwait(true);
        var item = _gallery.CurrentPage.First();
        var previous = item.Rating;
        byte target = previous == 2 ? (byte)3 : (byte)2;

        Task rating;
        using (var connection = _db.OpenSqliteConnection())
        using (var tx = connection.BeginTransaction()) // BEGIN IMMEDIATE: Schreibsperre wie „Neu aufbauen“
        {
            var clock = Stopwatch.StartNew();
            rating = _gallery.SetItemRatingCommand.ExecuteAsync(new RatingRequest(item, target));
            Check(clock.Elapsed < TimeSpan.FromSeconds(1), $"Bewertung kehrt sofort zurück ({clock.ElapsedMilliseconds} ms)");
            Check(item.Rating == target, "Kachel zeigt die Bewertung sofort");

            // Der Hinweis kommt nach dem ersten gescheiterten Versuch (busy_timeout 5 s) — WaitUntilAsync läuft dabei
            // selbst auf dem UI-Thread, der also bedienbar bleibt.
            await WaitUntilAsync(() => _statusBar.CatalogWriteWaitText is not null, ShortWait, "Statusleiste: Änderung wartet").ConfigureAwait(true);
            Check(_statusBar.CatalogWriteWaitText!.Contains("auf den Katalog", StringComparison.Ordinal), $"Statusleiste: „{_statusBar.CatalogWriteWaitText}“");
            Check(!rating.IsCompleted, "Bewertung wartet noch auf die Sperre");
            await SnapshotAsync("19-statusleiste-katalog-wartet").ConfigureAwait(true);
            tx.Commit();
        }

        await rating.WaitAsync(ShortWait).ConfigureAwait(true);
        await WaitUntilAsync(() => _statusBar.CatalogWriteWaitText is null, ShortWait, "Hinweis verschwindet").ConfigureAwait(true);
        var stored = await DbScalarAsync<long>("SELECT COALESCE(Rating, 0) FROM Files WHERE Id = $id", ("$id", item.Id)).ConfigureAwait(true);
        Check(stored == target && _gallery.ErrorMessage is null, $"DB: Bewertung {stored} nach der Freigabe (erwartet {target}), keine Fehlermeldung");

        await _gallery.SetItemRatingCommand.ExecuteAsync(new RatingRequest(item, previous)).ConfigureAwait(true);
    }
}
