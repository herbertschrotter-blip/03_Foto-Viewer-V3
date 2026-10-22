using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumora.Services.Assistant;
using Lumora.Shell;
using Lumora.Stories;
using Lumora.Stories.Assistant;
using Lumora.Wpf.Tests.Fixtures;
using Wpf.Ui.Appearance;

namespace Lumora.Wpf.Tests.Stories.Assistant;

/// <summary>
/// Story-Assistent in der Oberfläche (ADR-0063, Mockup 12): Leiste → Karte „So habe ich es verstanden“ (nichts entsteht
/// vor „Erstellen“), Rückfrage, Punkte ändern (neu zählen ohne Sprach-KI), Aktionen Story/Album/Diashow/Galerie/Collage,
/// Hinweis ohne Ollama, Verlauf; Einstellungen (Installation und Download nur nach Bestätigung mit Name/Quelle/Größe/Lizenz);
/// Collage-Dialog (Anzahl, Mischen, Tauschen, Speichern, versteckt nur im Container); echtes Rendern an Temp-Dateien;
/// Panel, Dialog und Einstellungs-Reiter ohne Binding-Fehler in hell und dunkel.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class StoryAssistantViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private static (StoryAssistantViewModel Vm, FakeAssistant Assistant, StoryTestKit Kit, FakeCollageDialogs Collage) Create(FakeRuntime? runtime = null)
    {
        var kit = new StoryTestKit();
        var assistant = new FakeAssistant();
        var collage = new FakeCollageDialogs();
        var vm = new StoryAssistantViewModel(assistant, kit.Services, runtime ?? new FakeRuntime { Status = new AssistantRuntimeStatus(AssistantRuntimeState.Ready, "m", "1") }, collage, messenger: kit.Messenger);
        return (vm, assistant, kit, collage);
    }

    private static AssistantPlan HerbertsPlan() => new()
    {
        Action = IntentAction.Album,
        Persons = [FakeAssistant.Person(1, "Nicole"), FakeAssistant.Person(2, "Nina")],
        Trip = new PlanTrip(true, 120, [new TermMatch(TermMatchKind.Category, "Reisen", [3], 40)]),
        Time = new PlanTime(new DateOnly(2021, 10, 22), new DateOnly(2026, 10, 21), null, null, "22.10.2021 – 21.10.2026", "letzte 5 Jahre bis heute"),
        BestSuggested = true,
    };

    [Fact]
    public Task Satz_zeigt_die_Karte_und_erst_Erstellen_legt_das_Album_an() => OnUiAsync(async () =>
    {
        var (vm, assistant, _, _) = Create();
        assistant.Understand = s => FakeAssistant.Understood(s, HerbertsPlan());
        assistant.Evaluate = p => FakeAssistant.ResultWith(80, p.Best);
        vm.Sentence = "Album mit den schönsten Urlaubsfotos der letzten fünf Jahre mit Nicole und Nina";

        await vm.AskAsync();

        vm.Card.Should().NotBeNull();
        vm.Question.Should().BeNull();
        vm.IsBusy.Should().BeFalse();
        vm.Plan!.Best.Should().Be(50, "„die schönsten“ ohne Zahl: Lumora schlägt eine Anzahl vor");
        var facts = vm.Card!.Facts.ToDictionary(f => f.Label);
        facts["Personen"].Tokens.Select(t => t.Text).Should().Equal("Nicole", "Nina", "alle zusammen auf dem Foto");
        facts["Zeitraum"].Tokens.Single().Text.Should().Be("22.10.2021 – 21.10.2026");
        facts["Urlaub"].Tokens.Select(t => t.Text).Should().Equal("Reise-Erkennung (120)", "Kategorie „Reisen“ (40)");
        facts["Auswahl"].Tokens.Single().Should().Match<AssistantToken>(t => t.Text == "die besten 50" && t.Detail == "Vorschlag von Lumora");
        facts["Auswahl"].Tokens.Single().Options.Select(o => o.Text).Should().Contain(["alle", "die besten 50", "die besten 100", "die besten 200"]);
        facts["Was entsteht"].Tokens.Single().Text.Should().Be("Album „Nicole und Nina · Urlaub 2023–2025“");
        vm.Card.CountText.Should().Be("80");
        vm.Card.ResultText.Should().Contain("davon die besten 50");
        assistant.Calls.Should().NotContain(c => c.StartsWith("album", StringComparison.Ordinal), "nichts entsteht ohne „Erstellen“");
        assistant.Calls.Should().Contain(c => c.StartsWith("remember:", StringComparison.Ordinal));

        // umschalten auf „mindestens eine“ → neu zählen ohne Sprach-KI
        var understandCalls = assistant.Calls.Count(c => c.StartsWith("understand", StringComparison.Ordinal));
        await facts["Personen"].Tokens[2].Options.Single(o => o.Text == "mindestens eine davon").ApplyCommand.ExecuteAsync(null);
        assistant.Evaluated.Last().PersonMatch.Should().Be(IntentMatch.Any);
        await vm.Card!.Facts.Single(f => f.Label == "Auswahl").Tokens.Single().Options.Single(o => o.Text == "die besten 100").ApplyCommand.ExecuteAsync(null);
        vm.Plan!.Best.Should().Be(100);
        assistant.Calls.Count(c => c.StartsWith("understand", StringComparison.Ordinal)).Should().Be(understandCalls);

        await vm.CreateAsync();
        assistant.Calls.Should().Contain(c => c.StartsWith("album:", StringComparison.Ordinal));
        vm.StatusText.Should().Contain("Album „Nicole und Nina · Urlaub 2023–2025“");
        vm.Card.Should().BeNull();
    });

    [Fact]
    public Task Rueckfrage_mit_Gesichtern_Antwort_loest_neu_auf() => OnUiAsync(async () =>
    {
        var (vm, assistant, _, _) = Create();
        var question = new AssistantQuestion(QuestionKind.Person, "oma", "Wer ist „Oma“?", "zwei passen",
            [new QuestionChoice("Maria", "Oma · 418 Fotos", [1], null, 10, 1), new QuestionChoice("Beide", "eine Auswahl mit allen", [1, 2])]);
        assistant.Understand = s => FakeAssistant.Understood(s, new AssistantPlan(), question);
        assistant.AfterAnswer = new AssistantResolution(new AssistantPlan { Persons = [FakeAssistant.Person(1, "Maria", "Oma")] }, []);
        vm.Sentence = "Weihnachten bei Oma, alle Jahre";

        await vm.AskAsync();
        vm.Question!.Title.Should().Be("Wer ist „Oma“?");
        vm.Question.Choices.Select(c => c.Label).Should().Equal("Maria", "Beide");
        vm.Question.Choices[0].HasFace.Should().BeTrue();
        vm.Card.Should().BeNull();

        await vm.Question.Choices[0].ChooseCommand.ExecuteAsync(null);
        assistant.Calls.Should().Contain("answer:oma:Maria").And.Contain("resolve:Weihnachten bei Oma, alle Jahre");
        vm.Question.Should().BeNull();
        vm.Card!.Facts.First().Tokens.First().Text.Should().Be("Maria");
    });

    [Fact]
    public Task Aktionen_Story_Diashow_Galerie_und_Collage() => OnUiAsync(async () =>
    {
        var (vm, assistant, kit, collage) = Create();
        var opened = new List<int>();
        vm.OpenStory = id => { opened.Add(id); return Task.CompletedTask; };
        async Task AskAs(IntentAction action)
        {
            assistant.Understand = s => FakeAssistant.Understood(s, new AssistantPlan { Action = action, AlsoStory = action == IntentAction.Collage, Persons = [FakeAssistant.Person(1, "Anna")] });
            vm.Sentence = "Satz " + action;
            await vm.AskAsync();
        }

        await AskAs(IntentAction.Story);
        await vm.CreateAsync();
        opened.Should().Equal(77);

        await AskAs(IntentAction.Slideshow);
        vm.Card!.CreateText.Should().Be("Diashow starten");
        await vm.CreateAsync();
        kit.Slideshow.Starts.Should().ContainSingle().Which.Ids.Should().Equal(1, 2, 3);

        await AskAs(IntentAction.Gallery);
        await vm.CreateAsync();
        kit.Navigation.Tags.Should().Contain(NavigationTags.Gallery);

        await AskAs(IntentAction.Collage);
        await vm.CreateAsync();
        collage.Requests.Should().ContainSingle().Which.Should().Match<CollageRequest>(r => r.Title == "Anna · 2023–2025" && r.CandidateIds.SequenceEqual(new[] { 1, 2, 3 }));
        assistant.Calls.Count(c => c.StartsWith("story:", StringComparison.Ordinal)).Should().Be(2, "Collage mit „+ Story“ legt auch die Story an");
        vm.StatusText.Should().Contain("Collage gespeichert");
    });

    [Fact]
    public Task Ohne_Ollama_Hinweis_mit_Weg_in_die_Einstellungen_und_Verlauf() => OnUiAsync(async () =>
    {
        var (vm, assistant, kit, _) = Create(new FakeRuntime { Status = new AssistantRuntimeStatus(AssistantRuntimeState.NotInstalled, "m") });
        assistant.History.Add(new AssistantHistoryItem(4, "Diashow: Papa und ich", StoryIntentJson.ToJson(new StoryIntent { Action = IntentAction.Slideshow }), IntentAction.Slideshow, 3, DateTime.UtcNow));

        await vm.RefreshAsync();
        vm.SetupHint.Should().Contain("nicht installiert").And.Contain("Anna 2019");
        vm.History.Should().ContainSingle().Which.ActionText.Should().Be("Wieder abspielen");
        vm.ShowHistory.Should().BeTrue();
        vm.OpenSettingsCommand.Execute(null);
        kit.Navigation.Tags.Should().Contain(NavigationTags.SettingsAssistant);

        assistant.Understand = _ => throw new AssistantException("Ollama läuft nicht.", needsSetup: true);
        vm.Sentence = "Weihnachten bei Oma";
        await vm.AskAsync();
        vm.ErrorText.Should().Be("Ollama läuft nicht.");
        vm.SetupHint.Should().Contain("Einstellungen › Assistent");

        // Verlauf „Wieder abspielen“: ohne Sprach-KI, Diashow startet direkt
        assistant.AfterAnswer = new AssistantResolution(new AssistantPlan { Action = IntentAction.Slideshow, Persons = [FakeAssistant.Person(1, "Franz")] }, []);
        var before = assistant.Calls.Count(c => c.StartsWith("understand", StringComparison.Ordinal));
        await vm.OpenHistoryAsync(vm.History[0]);
        assistant.Calls.Count(c => c.StartsWith("understand", StringComparison.Ordinal)).Should().Be(before);
        kit.Slideshow.Starts.Should().ContainSingle();
        await vm.DeleteHistoryAsync(vm.History[0]);
        assistant.Calls.Should().Contain("delete-history:4");
        vm.History.Should().BeEmpty();

        vm.Sentence = string.Empty;
        await vm.AskAsync();
        vm.ErrorText.Should().StartWith("Bitte einen Satz eingeben");
    });

    [Fact]
    public Task Einstellungen_Installation_und_Download_nur_nach_Bestaetigung() => OnUiAsync(async () =>
    {
        var assistant = new FakeAssistant();
        var runtime = new FakeRuntime { Status = new AssistantRuntimeStatus(AssistantRuntimeState.NotInstalled, AssistantModels.DefaultModel) };
        var installer = new FakeInstaller();
        var probe = new FakeProbe();
        var dialogs = new RecordingDialogs();
        assistant.Aliases.Add(new AssistantAliasInfo("oma", [1, 2], "Maria und Theresia"));
        var vm = new AssistantSettingsViewModel(assistant, runtime, installer, probe, dialogs, messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        await vm.LoadAsync();
        vm.CanInstallOllama.Should().BeTrue();
        vm.OllamaText.Should().Be("nicht installiert");
        vm.Aliases.Should().ContainSingle().Which.Text.Should().Be("„oma“ = Maria und Theresia");

        dialogs.ConfirmAnswer = false;
        await vm.InstallOllamaAsync();
        installer.Installs.Should().Be(0, "abgelehnt → nichts installiert");
        dialogs.Messages.Last().Should().Contain("Ollama.Ollama").And.Contain("Quelle:").And.Contain("Größe:").And.Contain("MIT License");
        dialogs.ConfirmAnswer = true;
        await vm.InstallOllamaAsync();
        installer.Installs.Should().Be(1);

        runtime.Status = new AssistantRuntimeStatus(AssistantRuntimeState.ModelMissing, AssistantModels.DefaultModel, "0.12");
        await vm.RefreshAsync();
        vm.CanDownloadModel.Should().BeTrue();
        dialogs.ConfirmAnswer = false;
        await vm.DownloadModelAsync();
        probe.Asked.Should().Equal(AssistantModels.DefaultModel);
        dialogs.Messages.Last().Should().Contain("qwen2.5:3b").And.Contain("1,9 GB").And.Contain("registry.ollama.ai").And.Contain("Qwen Research License");
        runtime.Pulls.Should().BeEmpty("abgelehnt → kein Download");
        dialogs.ConfirmAnswer = true;
        await vm.DownloadModelAsync();
        runtime.Pulls.Should().Equal(AssistantModels.DefaultModel);
        vm.IsModelReady.Should().BeTrue();

        vm.UseGpu = false;
        vm.RememberHistory = false;
        assistant.Settings.UseGpu.Should().BeFalse();
        assistant.Settings.RememberHistory.Should().BeFalse();
        await vm.ClearHistoryAsync();
        assistant.Calls.Should().Contain("clear-history");
        await vm.DeleteAliasAsync(vm.Aliases[0]);
        vm.Aliases.Should().BeEmpty();

        assistant.Understand = s => FakeAssistant.Understood(s, new AssistantPlan { Terms = [new PlanTerm("Hund", [new TermMatch(TermMatchKind.Motif, "Hund", [1], 12)]), new PlanTerm("See", [])] });
        await vm.TryAsync();
        vm.TryResult.Should().Contain("„Hund“: Motiv (12)").And.Contain("„See“: nicht gefunden");
        assistant.Calls.Should().NotContain(c => c.StartsWith("remember", StringComparison.Ordinal) || c.StartsWith("story", StringComparison.Ordinal), "Ausprobieren erstellt und merkt nichts");
    });

    [Fact]
    public Task Collage_Dialog_Anzahl_Mischen_Tauschen_Speichern() => OnUiAsync(async () =>
    {
        var service = new FakeCollageService();
        var picker = new FakeFilePicker();
        var vm = new CollageDialogViewModel(service, picker, new CollageRequest("Anna · 2024", Enumerable.Range(1, 100).ToList(), 36));
        await vm.LoadAsync();

        vm.SelectedPhotos.Should().HaveCount(36);
        vm.Tiles.Should().HaveCount(36);
        vm.TargetPath.Should().Be(@"C:\Bilder\Lumora\Collagen\Anna 2024.jpg");
        vm.SelectedPhotos[0].FileId.Should().Be(1, "das beste Foto ist das große Titelbild");
        vm.Tiles.Single(t => t.Index == 0).Width.Should().BeGreaterThan(vm.Tiles.Single(t => t.Index == 1).Width);

        vm.ChooseCommand.Execute(vm.Counts.Single(c => c.Label == "12"));
        vm.SelectedPhotos.Should().HaveCount(12);
        var before = vm.SelectedPhotos.Select(p => p.FileId).ToList();
        vm.Remix();
        vm.SelectedPhotos.Select(p => p.FileId).Should().NotEqual(before, "Neu mischen wählt andere Fotos");
        var swapped = vm.Tiles[3];
        vm.Swap(swapped);
        vm.Tiles.Single(t => t.Index == swapped.Index).FileId.Should().NotBe(swapped.FileId);

        vm.ChooseCommand.Execute(vm.Formats.Single(c => c.Label == "A4 hoch"));
        vm.ChooseCommand.Execute(vm.Sizes.Single(c => c.Label == "Druck 300 dpi"));
        vm.SizeText.Should().Be("2481 × 3508 Pixel · 300 dpi");
        vm.PreviewHeight.Should().BeGreaterThan(CollageDialogViewModel.PreviewWidth);
        vm.ShowDates = true;

        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        await vm.SaveAsync();
        service.Saves.Should().BeEmpty("ohne bestätigten Speicherort (Dateidialog abgebrochen) wird nichts gespeichert");
        picker.Path = @"C:\Bilder\Lumora\Collagen\Anna 2024.jpg";
        service.Allowed = false;
        await vm.SaveAsync();
        vm.ErrorText.Should().Contain("Container");
        service.Saves.Should().BeEmpty();
        service.Allowed = true;
        await vm.SaveAsync();
        var save = service.Saves.Single();
        save.Options.Should().Be(new CollageOptions(CollageFormat.A4Portrait, CollageSize.Print300, 12, CollageArrangement.Hero, true, true));
        save.Title.Should().Be("Anna · 2024");
        vm.Result!.Width.Should().Be(2481);
        closed.Should().Be(1);
    });

    [Fact]
    public Task Story_Ansicht_Als_Collage_Bild_speichern_ist_aktiv() => OnUiAsync(async () =>
    {
        var kit = new StoryTestKit();
        kit.Stories.Add(FakeStoryService.Card(1, Lumora.Domain.Enums.StoryKind.Trip, "12.–19. Juli 2025", days: 3), dayCount: 3, perDay: 9);
        var collage = new FakeCollageDialogs();
        var services = new StoryPageServices(kit.Stories, kit.Media, kit.Images, kit.Dialogs, kit.Navigation, null, kit.Slideshow, kit.Lightbox, null, kit.Bridge, null, collage);
        var vm = new StoryViewModel(services, 1, () => { }, Microsoft.Extensions.Logging.Abstractions.NullLogger<StoryViewModel>.Instance, kit.Messenger);
        await vm.LoadAsync();

        vm.CanSaveCollage.Should().BeTrue();
        vm.CollageHint.Should().BeNull();
        await vm.SaveCollageCommand.ExecuteAsync(null);
        collage.Requests.Should().ContainSingle().Which.Title.Should().Be("12.–19. Juli 2025");
        vm.StatusText.Should().StartWith("Collage gespeichert");
    });

    [Fact]
    public async Task Rendern_aus_Originalen_in_Temp_Dateien()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "collage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var photos = new List<CollagePhoto>();
            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(folder, $"f{i}.jpg");
                WriteJpeg(path, i % 2 == 0 ? 400 : 300, i % 2 == 0 ? 300 : 400, Color.FromRgb((byte)(40 * i), 120, 200));
                photos.Add(new CollagePhoto(i, path, i % 2 == 0 ? 4.0 / 3 : 3.0 / 4, new DateTime(2024, 7, 1 + i)));
            }

            photos.Add(new CollagePhoto(99, Path.Combine(folder, "fehlt.jpg"), 1.0, null)); // unlesbar → graue Fläche
            var renderer = new WpfCollageRenderer();
            foreach (var (size, format, ext, dpi) in new[] { (CollageSize.FullHd, CollageImageFormat.Jpeg, "jpg", 96), (CollageSize.Print300, CollageImageFormat.Png, "png", 300) })
            {
                var (w, h, d) = CollageLayout.Dimensions(CollageFormat.A4Landscape, size);
                var layout = CollageLayout.Compute(w, h, d, photos.Select(p => p.Aspect).ToList(), CollageArrangement.Hero, showTitle: true);
                var target = Path.Combine(folder, "out", "Collage." + ext);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await renderer.RenderAsync(new CollageRenderJob(layout, photos, "Anna · 2024", "6 Fotos", ShowDates: true, target, format), null, CancellationToken.None);

                using (var stream = File.OpenRead(target))
                {
                    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                    frame.PixelWidth.Should().Be(w);
                    frame.PixelHeight.Should().Be(h);
                    frame.DpiX.Should().BeApproximately(dpi, 0.5);
                }

                Directory.GetFiles(Path.GetDirectoryName(target)!).Should().HaveCount(1, "keine Temp-Reste");
                File.Delete(target);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void Panel_Dialog_und_Einstellungen_ohne_Binding_Fehler(ApplicationTheme theme)
    {
        TestUiThread.Run(
            () =>
            {
                var previous = ApplicationThemeManager.GetAppTheme();
                ApplicationThemeManager.Apply(theme);
                var (vm, assistant, _, _) = Create();
                assistant.Understand = s => FakeAssistant.Understood(s, HerbertsPlan() with { Terms = [new PlanTerm("Barcelona", [new TermMatch(TermMatchKind.Tag, "Barcelona", [1], 212), new TermMatch(TermMatchKind.Place, "Barcelona, Spanien", [2], 180, true, 41.4, 2.2, 20)]), new PlanTerm("Hund", [])] });
                assistant.History.Add(new AssistantHistoryItem(1, "Story von allen Sonnenuntergängen am Meer", "{\"action\":\"story\"}", IntentAction.Story, 3, DateTime.UtcNow));
                var settings = new AssistantSettingsViewModel(assistant, new FakeRuntime(), new FakeInstaller(), new FakeProbe(), new RecordingDialogs(), messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
                var dialogVm = new CollageDialogViewModel(new FakeCollageService(), new FakeFilePicker(), new CollageRequest("Anna · 2024", Enumerable.Range(1, 40).ToList()));
                using var errors = new BindingErrors();
                var panel = new StoryAssistantPanel { DataContext = vm };
                var section = new SettingsAssistantSection { DataContext = settings };
                var window = new Window { Width = 1200, Height = 900, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual };
                var dialog = new CollageDialogWindow(dialogVm) { ShowActivated = false, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
                try
                {
                    window.Content = panel;
                    window.Show();
                    _ = vm.RefreshAsync();
                    PumpUntil(() => vm.History.Count == 1);
                    vm.ShowHistory.Should().BeTrue();
                    vm.Sentence = "Fotos aus Barcelona";
                    _ = vm.AskAsync();
                    PumpUntil(() => vm.Card is not null);
                    window.UpdateLayout();
                    DoEvents();
                    vm.Card!.Facts.Single(f => f.Label == "Begriffe").Tokens.Select(t => t.Detail).Should().Equal("Tag (212) · Ort (180)", "nicht gefunden");
                    vm.ShowExamples.Should().BeFalse();

                    window.Content = section;
                    window.UpdateLayout();
                    PumpUntil(() => settings.ModelText != "wird geprüft …");
                    DoEvents();

                    dialog.Show();
                    PumpUntil(() => dialogVm.Tiles.Count > 0);
                    dialog.UpdateLayout();
                    DoEvents();
                    errors.Messages.Should().BeEmpty();
                }
                finally
                {
                    dialog.Close();
                    window.Close();
                    ApplicationThemeManager.Apply(previous);
                }

                return 0;
            },
            Timeout);
    }

    private static void WriteJpeg(string path, int width, int height, Color color)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

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
