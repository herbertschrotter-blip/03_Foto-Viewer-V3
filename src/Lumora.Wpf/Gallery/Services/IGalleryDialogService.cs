using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lumora.Gallery.Services;

/// <summary>
/// Dialoge der Galerie (Bestätigung, Fehlerliste) hinter einem Interface, damit das ViewModel
/// keine Fenster-Klassen kennt und testbar bleibt. Implementierung: Wpf.Ui MessageBox.
/// </summary>
public interface IGalleryDialogService
{
    /// <summary>Ja/Nein-Rückfrage; true, wenn der Benutzer die primäre Aktion bestätigt hat.</summary>
    Task<bool> ConfirmAsync(string title, string message, string primaryButtonText, bool isDestructive = false);

    /// <summary>Zeigt eine Meldung mit einer Liste von Detailzeilen (z. B. fehlgeschlagene Dateien).</summary>
    Task ShowErrorsAsync(string title, string message, IReadOnlyList<string> details);
}
