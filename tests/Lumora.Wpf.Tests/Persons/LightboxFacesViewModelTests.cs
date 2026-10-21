using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Domain.Enums;
using Lumora.Lightbox.Controls;
using Lumora.Lightbox.Faces;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Indexing;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Persons;

/// <summary>
/// Gesichter in der Lightbox (0.2.38) gegen den Fake-Zuordnungsdienst: Umschalter (Taste F) lädt Rahmen mit
/// Beschriftung, Menü je Rahmen (bestätigen, ablehnen, Person wählen oder neu anlegen, kein Gesicht), Neu-Laden nach
/// Änderungen, Rahmen wandern mit dem Zoom des Bildes. Der Fake liefert erledigte Tasks — die Abläufe laufen synchron.
/// </summary>
public sealed class LightboxFacesViewModelTests
{
    private const int Photo = 7;
    private readonly FakeFaceRecognitionService _service = new();
    private readonly List<string> _status = new();
    private readonly int _anna;
    private readonly int _bert;

    public LightboxFacesViewModelTests()
    {
        _anna = _service.AddPerson("Anna", 5);
        _bert = _service.AddPerson("Bert", 3);
        _service.AddPerson("Versteckt", 9, hidden: true);
        _service.AddFace(Photo, FaceState.Confirmed, personId: _anna, x: 0.1f);
        _service.AddFace(Photo, FaceState.Suggested, suggestedId: _bert, x: 0.4f);
        _service.AddFace(Photo, FaceState.Unconfirmed, x: 0.7f);
        _service.AddFace(Photo, FaceState.Ignored, x: 0.9f);
    }

    private LightboxFacesViewModel Create(FakePersonDialogs? dialogs = null, IMessenger? messenger = null)
        => new(_service.AsService(), dialogs ?? new FakePersonDialogs(), messenger: messenger ?? new WeakReferenceMessenger(), dispatcher: Dispatcher.CurrentDispatcher)
        {
            ShowStatus = _status.Add,
        };

    private static LightboxFaceBox Box(LightboxFacesViewModel vm, LightboxFaceKind kind) => vm.Boxes.Single(b => b.Kind == kind);

    [Fact]
    public async Task Umschalter_laedt_Rahmen_mit_Name_Vorschlag_und_Unbekannt_ohne_ignorierte()
    {
        var vm = Create();
        await vm.SetFileAsync(Photo, isImage: true);
        vm.Boxes.Should().BeEmpty("ausgeschaltet wird nichts geladen");

        await vm.ToggleAsync();

        vm.IsVisible.Should().BeTrue();
        vm.ShowBoxes.Should().BeTrue();
        vm.Boxes.Select(b => b.Label).Should().Equal("Anna", "Vorschlag: Bert?", "Unbekannt");
        Box(vm, LightboxFaceKind.Confirmed).Box.Should().Be(new Rect(0.1, 0.2, 0.2, 0.3).WithFloatPrecision());
        _status.Last().Should().Contain("3 Gesichter");

        await vm.ToggleAsync();
        vm.IsVisible.Should().BeFalse();
        vm.Boxes.Should().BeEmpty();
    }

    [Fact]
    public async Task Blaettern_laedt_die_Rahmen_des_neuen_Fotos_Videos_zeigen_keine()
    {
        var vm = Create();
        await vm.SetFileAsync(Photo, isImage: true);
        await vm.ToggleAsync();
        vm.Boxes.Should().HaveCount(3);

        await vm.SetFileAsync(99, isImage: true);
        vm.Boxes.Should().BeEmpty();
        vm.IsVisible.Should().BeTrue("der Umschalter bleibt beim Blättern an");

        await vm.SetFileAsync(Photo, isImage: false);
        vm.ShowBoxes.Should().BeFalse();
        vm.Boxes.Should().BeEmpty();
    }

    [Fact]
    public async Task Menue_eines_Vorschlags_bestaetigt_ablehnt_und_ignoriert()
    {
        var vm = Create();
        await vm.SetFileAsync(Photo, isImage: true);
        await vm.ToggleAsync();

        var menu = await vm.BuildMenuAsync(Box(vm, LightboxFaceKind.Suggested));
        menu.Select(m => m.IsSeparator ? "-" : m.Header).Should().Equal("Ist Bert", "Nicht Bert", "-", "Ist …", "Kein Gesicht");

        await menu[0].Execute!();
        _service.Calls.Should().Contain(c => c.StartsWith("confirm ", StringComparison.Ordinal) && c.EndsWith($" {_bert}", StringComparison.Ordinal));
        vm.Boxes.Select(b => b.Label).Should().Equal("Anna", "Bert", "Unbekannt");
        _status.Last().Should().Contain("„Bert“ bestätigt");

        var bert = vm.Boxes.Single(b => b.Label == "Bert");
        var ignore = (await vm.BuildMenuAsync(bert)).Single(m => m.Header == "Kein Gesicht");
        await ignore.Execute!();
        vm.Boxes.Select(b => b.Label).Should().Equal("Anna", "Unbekannt");
    }

    [Fact]
    public async Task Nicht_X_lehnt_den_Vorschlag_ab()
    {
        var vm = Create();
        await vm.SetFileAsync(Photo, isImage: true);
        await vm.ToggleAsync();

        var menu = await vm.BuildMenuAsync(Box(vm, LightboxFaceKind.Suggested));
        await menu.Single(m => m.Header == "Nicht Bert").Execute!();

        _service.Calls.Should().Contain(c => c.StartsWith("reject ", StringComparison.Ordinal));
        vm.Boxes.Select(b => b.Label).Should().Equal("Anna", "Unbekannt", "Unbekannt");
    }

    [Fact]
    public async Task Ist_Untermenue_zeigt_sichtbare_Personen_ohne_die_eigene_und_legt_neue_an()
    {
        var vm = Create(new FakePersonDialogs(prompt: "  carla  "));
        await vm.SetFileAsync(Photo, isImage: true);
        await vm.ToggleAsync();

        var confirmedMenu = await vm.BuildMenuAsync(Box(vm, LightboxFaceKind.Confirmed));
        confirmedMenu.Select(m => m.Header).Should().Equal("Ist …", "Kein Gesicht");
        confirmedMenu[0].Children!.Select(c => c.IsSeparator ? "-" : c.Header).Should().Equal("Bert", "-", "Andere oder neue Person…");

        var unknown = Box(vm, LightboxFaceKind.Unknown);
        var choices = (await vm.BuildMenuAsync(unknown)).Single(m => m.Header == "Ist …").Children!;
        choices.Select(c => c.IsSeparator ? "-" : c.Header).Should().Equal("Anna", "Bert", "-", "Andere oder neue Person…");

        await choices.Last().Execute!(); // Eingabe „carla“ → neue Person „carla“
        _service.Calls.Should().Contain(c => c.StartsWith("new ", StringComparison.Ordinal) && c.EndsWith(" carla", StringComparison.Ordinal));
        vm.Boxes.Select(b => b.Label).Should().Contain("carla");
    }

    [Fact]
    public async Task Name_einer_vorhandenen_Person_ordnet_ihr_zu_ohne_neue_anzulegen()
    {
        var vm = Create();
        await vm.SetFileAsync(Photo, isImage: true);
        await vm.ToggleAsync();

        await vm.AssignByNameAsync(Box(vm, LightboxFaceKind.Unknown), "ANNA");
        await vm.AssignByNameAsync(vm.Boxes[0], "   ");

        _service.Calls.Should().NotContain(c => c.StartsWith("new ", StringComparison.Ordinal));
        _service.Calls.Should().Contain(c => c.StartsWith("confirm ", StringComparison.Ordinal) && c.EndsWith($" {_anna}", StringComparison.Ordinal));
        vm.Boxes.Count(b => b.Label == "Anna").Should().Be(2);
    }

    [Fact]
    public void Aenderungsnachrichten_laden_die_Rahmen_neu()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = Create(messenger: messenger);
        vm.SetFileAsync(Photo, isImage: true).IsCompleted.Should().BeTrue();
        vm.ToggleAsync().IsCompleted.Should().BeTrue();
        vm.Boxes.Should().HaveCount(3);

        _service.AddFace(Photo, FaceState.Unconfirmed, x: 0.5f);
        messenger.Send(new PersonsChangedMessage(1));
        vm.Boxes.Should().HaveCount(4);

        _service.AddFace(Photo, FaceState.Unconfirmed, x: 0.55f);
        messenger.Send(new FacesChangedMessage(1, [12345]));
        vm.Boxes.Should().HaveCount(4, "andere Datei");
        messenger.Send(new FacesChangedMessage(1, [Photo]));
        vm.Boxes.Should().HaveCount(5);

        vm.Dispose();
        _service.AddFace(Photo, FaceState.Unconfirmed, x: 0.6f);
        messenger.Send(new PersonsChangedMessage(1));
        vm.Boxes.Should().HaveCount(5, "nach Dispose abgemeldet");
    }

    [Fact]
    public async Task Ohne_Dienst_bleibt_der_Umschalter_wirkungslos()
    {
        var status = new List<string>();
        var vm = new LightboxFacesViewModel(null) { ShowStatus = status.Add };
        await vm.SetFileAsync(Photo, isImage: true);
        await vm.ToggleAsync();
        vm.IsVisible.Should().BeFalse();
        vm.IsAvailable.Should().BeFalse();
        status.Should().ContainSingle().Which.Should().Contain("nicht verfügbar");
    }
}

/// <summary>Rahmen wandern mit Zoom und Schwenk (ZoomPanControl.Overlay + FaceBoxesLayer).</summary>
[Collection(UiTestCollection.Name)]
public sealed class FaceBoxesLayerTests
{
    [Fact]
    public void Rahmen_liegen_relativ_zum_Bild_und_folgen_dem_Zoom()
    {
        var result = TestUiThread.Run(() =>
        {
            var service = new FakeFaceRecognitionService();
            var anna = service.AddPerson("Anna");
            service.AddFace(1, FaceState.Confirmed, personId: anna, x: 0.25f);
            var vm = new LightboxFacesViewModel(service.AsService(), messenger: new WeakReferenceMessenger());
            vm.SetFileAsync(1, isImage: true).Wait();
            vm.ToggleAsync().Wait();

            var layer = new FaceBoxesLayer { Controller = vm };
            var control = new ZoomPanControl { Overlay = layer };
            var window = new Window
            {
                Width = 900,
                Height = 700,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -5000,
                Top = -5000,
                Content = control,
            };
            window.Show();
            try
            {
                control.Source = new WriteableBitmap(400, 300, 96, 96, PixelFormats.Bgr32, null);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                var fit = (Image: control.ImageRect, Frame: layer.FrameBounds(0));
                control.ZoomIn();
                control.ZoomIn();
                var zoomed = (Image: control.ImageRect, Frame: layer.FrameBounds(0));
                vm.ToggleAsync().Wait(); // aus → keine Rahmen mehr (Neuaufbau gebündelt nach dem Durchlauf)
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                return (fit, zoomed, layer.FrameCount);
            }
            finally
            {
                window.Close();
            }
        }, TimeSpan.FromSeconds(15));

        foreach (var (image, frame) in new[] { result.fit, result.zoomed })
        {
            image.IsEmpty.Should().BeFalse();
            frame.X.Should().BeApproximately(image.X + 0.25 * image.Width, 0.5);
            frame.Y.Should().BeApproximately(image.Y + 0.2 * image.Height, 0.5);
            frame.Width.Should().BeApproximately(0.2 * image.Width, 0.5);
            frame.Height.Should().BeApproximately(0.3 * image.Height, 0.5);
        }

        result.zoomed.Image.Width.Should().BeGreaterThan(result.fit.Image.Width * 1.4, "zweimal vergrößert");
        result.FrameCount.Should().Be(0);
    }
}

internal static class RectTestExtensions
{
    /// <summary>Box kommt als float aus dem Dienst — für Vergleiche auf float-Genauigkeit runden.</summary>
    public static Rect WithFloatPrecision(this Rect rect) => new((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
}
