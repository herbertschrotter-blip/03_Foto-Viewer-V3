using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Lumora.Shell;
using Lumora.Shell.Dialogs;

namespace Lumora.Gallery.Services;

/// <summary>Implementierung von <see cref="IGalleryDialogService"/> über das eigene <see cref="AppDialogWindow"/>.</summary>
public sealed class GalleryDialogService : IGalleryDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string primaryButtonText, bool isDestructive = false)
    {
        var outcome = AppDialogWindow.Show(ResolveOwner(), new DialogRequest(title, message)
        {
            PrimaryText = primaryButtonText,
            PrimaryIsDestructive = isDestructive,
        });
        return Task.FromResult(outcome.Choice == DialogChoice.Primary);
    }

    public Task ShowErrorsAsync(string title, string message, IReadOnlyList<string> details)
    {
        _ = AppDialogWindow.Show(ResolveOwner(), new DialogRequest(title, message)
        {
            CloseText = "Schließen",
            Details = details.Count > 0 ? string.Join("\n", details) : null,
        });
        return Task.CompletedTask;
    }

    private static Window? ResolveOwner()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
    }
}
