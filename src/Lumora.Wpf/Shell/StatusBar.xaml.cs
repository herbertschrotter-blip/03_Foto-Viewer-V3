using System.Windows.Controls;

namespace Lumora.Shell;

/// <summary>Statusleiste (ADR-0013); DataContext = StatusBarViewModel, gesetzt vom MainWindow.</summary>
public partial class StatusBar : UserControl
{
    public StatusBar()
    {
        InitializeComponent();
    }
}
