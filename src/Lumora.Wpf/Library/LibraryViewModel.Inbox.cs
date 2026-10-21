using System.Linq;
using System.Threading.Tasks;

namespace Lumora.Libraries;

/// <summary>
/// Bibliotheksverwaltung für Aufrufer außerhalb von Einstellungen › Bibliotheken &amp; Eingänge (ADR-0042): „Eingang
/// hinzufügen…“ auf der Seite „Ordner“ (gleicher Ablauf wie der Knopf in den Einstellungen) und die Frage, ob dort gerade
/// ein Scan läuft (der Bibliotheks-Umschalter wechselt dann nicht).
/// </summary>
public interface IInboxActions
{
    /// <summary>true, solange die Bibliotheksverwaltung scannt (Aktualisieren, Neu aufbauen).</summary>
    bool IsScanning { get; }

    /// <summary>
    /// Eingang zur aktiven Bibliothek hinzufügen (Ordnerauswahl, Prüfung, Angebot „Jetzt aktualisieren“); Hinweise und
    /// Fehler erscheinen als Dialog. true, wenn ein Eingang dazukam.
    /// </summary>
    Task<bool> AddInboxToActiveLibraryAsync();
}

public sealed partial class LibraryViewModel : IInboxActions
{
    private const string AddInboxTitle = "Eingang hinzufügen";

    public async Task<bool> AddInboxToActiveLibraryAsync()
    {
        if (IsScanning)
        {
            await _dialogs.ShowErrorAsync(AddInboxTitle, "Gerade läuft ein Scan der Bibliothek. Bitte warten, bis er fertig ist.").ConfigureAwait(true);
            return false;
        }

        ErrorMessage = null;
        await LoadAsync().ConfigureAwait(true);
        if (ErrorMessage is { } loadError)
        {
            await _dialogs.ShowErrorAsync(AddInboxTitle, loadError).ConfigureAwait(true);
            return false;
        }

        var active = Libraries.FirstOrDefault(l => l.IsActive);
        if (active is null)
        {
            await _dialogs.ShowErrorAsync(
                AddInboxTitle,
                "Es gibt noch keine Bibliothek. Lege zuerst unter Einstellungen › Bibliotheken & Eingänge eine an.").ConfigureAwait(true);
            return false;
        }

        var before = active.Roots.Count;
        await AddInboxRootAsync(active).ConfigureAwait(true);
        var added = Libraries.FirstOrDefault(l => l.Id == active.Id) is { } refreshed && refreshed.Roots.Count > before;

        // Fehler zeigen — beim Hinzufügen (dann kam nichts dazu) oder beim anschließenden Aktualisieren (Eingang steht trotzdem)
        if (ErrorMessage is { } error)
        {
            await _dialogs.ShowErrorAsync(added ? "Aktualisieren" : AddInboxTitle, error).ConfigureAwait(true);
        }

        return added;
    }
}
