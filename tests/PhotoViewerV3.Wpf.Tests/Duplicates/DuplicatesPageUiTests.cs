using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoViewerV3.Duplicates;
using PhotoViewerV3.Services.Duplicates;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Duplicates;

/// <summary>
/// Echte Seite „Doppelte“ im Fenster (Test-UI-Thread) mit Fake-Daten: XAML lädt in hell und dunkel ohne Binding-Fehler,
/// Gruppen sind virtualisiert, sichtbare Gruppen fordern Vorschaubilder an, der Radio-Knopf wählt die behaltene Datei;
/// Navigationseintrag und Symbole existieren.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class DuplicatesPageUiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void Seite_laedt_virtualisiert_und_waehlt_per_Radio_Knopf()
    {
        TestUiThread.Run(
            () =>
            {
                var f = new FakeDuplicates();
                for (var i = 0; i < 300; i++)
                {
                    f.Group(DuplicateMode.Exact, FakeDuplicates.Member(1000 + (i * 2)), FakeDuplicates.Member(1001 + (i * 2), inbox: true));
                }

                var vm = f.CreateViewModel();
                var page = new DuplicatesPage(vm);
                using var errors = new BindingErrors();
                var window = new Window
                {
                    Width = 1200,
                    Height = 800,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -5000,
                    Top = -5000,
                    Content = page,
                };
                try
                {
                    window.Show();
                    PumpUntil(() => !vm.IsLoading && vm.Groups.Count == 300);
                    window.UpdateLayout();
                    DoEvents();

                    var list = (ListBox)page.FindName("GroupList");
                    list.Items.Count.Should().Be(300);
                    var realized = Enumerable.Range(0, 300).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null);
                    realized.Should().BeGreaterThan(0).And.BeLessThan(40, "Gruppen sind virtualisiert");
                    f.RequestedImages.Should().Contain(1000).And.NotContain(1599, "nur sichtbare Gruppen holen Vorschaubilder");

                    var radios = FindAll<RadioButton>(list).Where(r => r.DataContext is DuplicateMemberViewModel).ToList();
                    radios.Should().NotBeEmpty();
                    var inboxRadio = radios.First(r => ((DuplicateMemberViewModel)r.DataContext).FileId == 1001);
                    inboxRadio.IsChecked.Should().BeFalse();
                    inboxRadio.IsChecked = true;
                    DoEvents();
                    vm.Groups[0].Keeper!.FileId.Should().Be(1001);
                    radios.First(r => ((DuplicateMemberViewModel)r.DataContext).FileId == 1000).IsChecked.Should().BeFalse();

                    errors.Messages.Should().BeEmpty("keine Binding-Fehler");
                }
                finally
                {
                    window.Close();
                }

                return 0;
            },
            Timeout);
    }

    [Fact]
    public void Navigation_und_Symbole()
    {
        NavigationTags.Duplicates.Should().Be("duplicates");
        NavigationTags.IsSettingsSection(NavigationTags.Duplicates).Should().BeFalse("eigene Seite in der Leiste");
        foreach (var symbol in new[] { "DocumentCopy24", "ImageMultiple24", "ArrowDownload24", "ArrowUndo24", "Delete24" })
        {
            Enum.TryParse<global::Wpf.Ui.Controls.SymbolRegular>(symbol, out _).Should().BeTrue(symbol);
        }

        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PhotoViewerV3.Wpf", "MainWindow.xaml"));
        xaml.Should().Contain("TargetPageTag=\"duplicates\"");
        xaml.IndexOf("TargetPageTag=\"places\"", StringComparison.Ordinal).Should().BeLessThan(xaml.IndexOf("TargetPageTag=\"duplicates\"", StringComparison.Ordinal));
    }

    /// <summary>Sammelt WPF-Binding-Fehler während des Tests.</summary>
    private sealed class BindingErrors : TraceListener
    {
        public BindingErrors()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }

        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotoViewerV3.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindAll<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            DoEvents();
            Thread.Sleep(5);
        }

        condition().Should().BeTrue("Bedingung innerhalb von 15 s");
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
