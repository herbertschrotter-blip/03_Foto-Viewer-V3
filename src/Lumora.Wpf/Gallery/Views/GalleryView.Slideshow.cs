using System.Windows.Controls;
using Lumora.Gallery.ViewModels;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Lumora.Gallery.Views;

/// <summary>Kontextmenü der Kachel: „Diashow“ (ADR-0059) — die Auswahl, zu der die Kachel beim Rechtsklick gehört.</summary>
public partial class GalleryView
{
    private void AddSlideshowAction(ContextMenu menu, GalleryItem target)
    {
        if (!_viewModel.HasSlideshow)
        {
            return;
        }

        menu.Items.Add(new MenuItem
        {
            Header = _viewModel.SelectedCount > 1 ? $"Diashow ({_viewModel.SelectedCount:N0} ausgewählt)" : "Diashow ab hier",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Play24 },
            Command = _viewModel.StartSlideshowCommand,
            CommandParameter = target,
        });
        menu.Items.Add(new Separator());
    }
}
