using System.Windows.Controls;

namespace PhotoViewerV3.Shell;

/// <summary>Statusleiste (ADR-0013); DataContext = StatusBarViewModel, gesetzt vom MainWindow.</summary>
public partial class StatusBar : UserControl
{
    public StatusBar()
    {
        InitializeComponent();
    }
}
