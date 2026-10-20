using System.Windows.Automation.Peers;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ListView;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.ListView;

/// <summary>
/// CLAUDE.md-Regel (v0.2.0-Hänger): Einträge großer Listen bleiben im UI-Automation-Baum Blätter. Die Listenansicht
/// ersetzt deshalb den DataGrid-Peer (dessen Item-Peers je Zelle weitere Peers erzeugen) und den Zeilen-Peer.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GalleryDataGridAutomationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void Zeilen_Peer_ist_ein_Blatt_mit_dem_Dateinamen()
    {
        var (children, name, type) = TestUiThread.Run(
            () =>
            {
                var row = new GalleryDataGridRow { DataContext = new GalleryItem(Dto(7, "IMG_0007.jpg")) };
                var peer = UIElementAutomationPeer.CreatePeerForElement(row);
                return (peer.GetChildren(), peer.GetName(), peer.GetAutomationControlType());
            },
            Timeout);

        children.Should().BeNull();
        name.Should().Be("IMG_0007.jpg");
        type.Should().Be(AutomationControlType.DataItem);
    }

    [Fact]
    public void Grid_Peer_ist_kein_DataGridAutomationPeer_und_hat_ohne_realisierte_Zeilen_keine_Kinder()
    {
        var (isDataGridPeer, children, type) = TestUiThread.Run(
            () =>
            {
                var grid = new GalleryDataGrid
                {
                    ItemsSource = Enumerable.Range(1, 500).Select(i => new GalleryItem(Dto(i, $"IMG_{i:0000}.jpg"))).ToList(),
                };
                var peer = UIElementAutomationPeer.CreatePeerForElement(grid);
                return (peer is DataGridAutomationPeer, peer.GetChildren(), peer.GetAutomationControlType());
            },
            Timeout);

        isDataGridPeer.Should().BeFalse();
        children.Should().BeNull("ohne Layout gibt es keine Zeilen-Container — und keine Item-/Zellen-Peers für 500 Einträge");
        type.Should().Be(AutomationControlType.DataGrid);
    }

    private static GalleryFileDto Dto(int id, string name)
        => new(id, 1, name, ".jpg", 2048, 1_700_000_000, 0, false, null, false, @"C:\Fotos");
}
