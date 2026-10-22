using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Assistant;
using Lumora.Services.Messaging;
using Lumora.Shell;

namespace Lumora.Stories.Assistant;

/// <summary>Einstellungen › Assistent geändert (Leiste ein/aus, Status neu prüfen).</summary>
public sealed record AssistantSettingsChangedMessage(AssistantSettings Settings);

/// <summary>
/// Story-Assistent auf der Seite Storys (Mockup 12, ADR-0063): Eingabeleiste mit Beispielen und Hinweis „läuft nur auf
/// diesem PC“, Fortschritt (abbrechbar), Karte „So habe ich es verstanden“ mit änderbaren Punkten, Trefferzahl und
/// Vorschau, Rückfrage-Karte, „Zuletzt gefragt“. Nichts entsteht ohne „Erstellen“; die Sprach-KI wird nur für den Satz
/// gefragt, jede Änderung in der Karte zählt nur in Lumora neu.
/// </summary>
public sealed partial class StoryAssistantViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private readonly IStoryAssistant _assistant;
    private readonly IAssistantRuntime? _runtime;
    private readonly StoryPageServices _services;
    private readonly ICollageDialogService? _collage;
    private readonly ILogger _logger;
    private readonly IMessenger _messenger;
    private CancellationTokenSource? _askCts;
    private AssistantUnderstanding? _understanding;
    private AssistantAnswers _answers = new();
    private AssistantPlan? _plan;
    private AssistantResult? _result;
    private AssistantQuestion? _openQuestion;
    private bool _remembered;
    private bool _playWhenReady;
    private IReadOnlyList<AssistantPerson>? _persons;

    /// <summary>Zählt bei jedem Zurücksetzen hoch: Ergebnisse älterer Anfragen (anderer Satz, andere Bibliothek) werden verworfen.</summary>
    private int _generation;

    public StoryAssistantViewModel(
        IStoryAssistant assistant,
        StoryPageServices services,
        IAssistantRuntime? runtime = null,
        ICollageDialogService? collage = null,
        ILogger<StoryAssistantViewModel>? logger = null,
        IMessenger? messenger = null)
    {
        _assistant = assistant;
        _services = services;
        _runtime = runtime;
        _collage = collage;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _services.Images.ImagesReady += OnImagesReady;
        if (_services.Faces is not null)
        {
            _services.Faces.ImagesReady += OnFacesReady;
        }

        _messenger.Register<StoryAssistantViewModel, AssistantSettingsChangedMessage>(this, static (r, m) => UiThread.Post(() => r.IsEnabled = m.Settings.Enabled));
        _messenger.Register<StoryAssistantViewModel, ActiveLibraryChangedMessage>(this, static (r, _) => UiThread.Post(r.OnLibraryChanged));
    }

    /// <summary>Eigene Story öffnen (setzt die Story-Seite).</summary>
    public Func<int, Task>? OpenStory { get; set; }

    public IReadOnlyList<string> Examples { get; } =
    [
        "Collage mit meiner Schwester bis 20 Jahre",
        "Story von allen Sonnenuntergängen am Meer",
        "Diashow: Papa und ich, die besten 50",
        "Album mit allen Fotos vom Hund 2022",
        "Weihnachten bei Oma, alle Jahre",
    ];

    public ObservableCollection<AssistantHistoryRow> History { get; } = new();

    [ObservableProperty] private bool _isEnabled = true;

    [ObservableProperty] private string _sentence = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty] private string? _busyText;

    [ObservableProperty] private string? _errorText;

    [ObservableProperty] private string? _statusText;

    /// <summary>Ollama/Modell fehlt — Hinweis mit Weg in Einstellungen › Assistent.</summary>
    [ObservableProperty] private string? _setupHint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHistory), nameof(ShowExamples))]
    private AssistantCard? _card;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHistory), nameof(ShowExamples))]
    private AssistantQuestionCard? _question;

    /// <summary>Beispiele nur ohne offene Karte (Platz für die Karte auf kleinen Bildschirmen).</summary>
    public bool ShowExamples => Card is null && Question is null;

    public bool IsIdle => !IsBusy;

    public bool ShowHistory => Card is null && Question is null && History.Count > 0;

    /// <summary>Aktueller Plan (Tests).</summary>
    public AssistantPlan? Plan => _plan;

    /// <summary>Aktuelle Treffer (Tests).</summary>
    public AssistantResult? Result => _result;

    /// <summary>Die Leiste soll das Eingabefeld fokussieren („Satz ändern“).</summary>
    public event Action? FocusRequested;

    // ---- Laden --------------------------------------------------------------------------------------------------------

    /// <summary>Einstellungen, Zustand von Ollama und Verlauf (beim Anzeigen der Seite).</summary>
    public async Task RefreshAsync()
    {
        try
        {
            IsEnabled = (await _assistant.GetSettingsAsync()).Enabled;
            await LoadHistoryAsync();
            if (_runtime is not null && IsEnabled)
            {
                var status = await _runtime.GetStatusAsync();
                SetupHint = status.IsReady ? null : status.Text + " Einfache Sätze wie „Anna 2019“ verstehe ich auch ohne.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assistent: Zustand nicht gelesen");
        }
    }

    private async Task LoadHistoryAsync()
    {
        var items = await _assistant.GetHistoryAsync();
        History.Clear();
        foreach (var item in items.Take(3))
        {
            History.Add(new AssistantHistoryRow(item.Id, item.Sentence, item.IntentJson, item.Action == IntentAction.Slideshow, this));
        }

        OnPropertyChanged(nameof(ShowHistory));
    }

    private void OnLibraryChanged() => _ = OnLibraryChangedAsync();

    private async Task OnLibraryChangedAsync()
    {
        CancelAsk();
        Reset();
        _persons = null;
        await RefreshAsync();
    }

    // ---- Fragen -------------------------------------------------------------------------------------------------------

    [RelayCommand]
    public async Task AskAsync()
    {
        var text = Sentence.Trim();
        if (text.Length == 0)
        {
            ErrorText = "Bitte einen Satz eingeben, z. B. „" + Examples[0] + "“.";
            return;
        }

        CancelAsk();
        Reset();
        var cts = _askCts = new CancellationTokenSource();
        var generation = _generation;
        IsBusy = true;
        BusyText = "Die Sprach-KI liest Ihren Satz … (nur auf diesem PC)";
        try
        {
            var understanding = await _assistant.UnderstandAsync(text, cts.Token);
            if (generation == _generation && !cts.IsCancellationRequested)
            {
                await ApplyAsync(understanding, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
            {
                StatusText = "Abgebrochen.";
            }
        }
        catch (AssistantException ex)
        {
            ErrorText = ex.Message;
            if (ex.NeedsSetup)
            {
                SetupHint = "Für diesen Satz braucht es die Sprach-KI (Ollama). Einrichten unter Einstellungen › Assistent.";
            }
        }
        catch (Exception ex)
        {
            ErrorText = "Das hat nicht geklappt: " + ex.Message;
            _logger.LogError(ex, "Assistent: Anfrage fehlgeschlagen");
        }
        finally
        {
            if (ReferenceEquals(_askCts, cts))
            {
                IsBusy = false;
                BusyText = null;
                _askCts = null;
            }

            cts.Dispose();
        }
    }

    [RelayCommand]
    public void CancelAsk()
    {
        _askCts?.Cancel();
    }

    [RelayCommand]
    private Task UseExampleAsync(string? example)
    {
        if (string.IsNullOrWhiteSpace(example))
        {
            return Task.CompletedTask;
        }

        Sentence = example;
        return AskAsync();
    }

    /// <summary>„Ausprobieren“-Weg und Verlauf: Verständnis anwenden (Rückfrage oder Karte).</summary>
    private async Task ApplyAsync(AssistantUnderstanding understanding, CancellationToken ct)
    {
        _understanding = understanding;
        if (understanding.Resolution.Question is { } question)
        {
            ShowQuestion(question);
            return;
        }

        Question = null;
        _openQuestion = null;
        _plan = understanding.Resolution.Plan;
        await EvaluateAsync(ct);
    }

    private async Task EvaluateAsync(CancellationToken ct = default)
    {
        if (_plan is not { } plan)
        {
            return;
        }

        var generation = _generation;
        BusyText ??= "Lumora zählt die Treffer …";
        var result = await _assistant.EvaluateAsync(plan, ct);
        if (plan.BestSuggested && plan.Best is null && _assistant.SuggestBest(result.Count) is { } suggested)
        {
            plan = plan with { Best = suggested };
            result = await _assistant.EvaluateAsync(plan, ct);
        }

        if (generation != _generation)
        {
            return; // inzwischen zurückgesetzt (anderer Satz, andere Bibliothek)
        }

        _plan = plan;
        _result = result;
        BuildCard();
        if (!_remembered && _understanding is { } u)
        {
            _remembered = true;
            await _assistant.RememberAsync(u.Sentence, u.Intent, _plan.Action, result.Count, ct);
        }

        if (_playWhenReady)
        {
            _playWhenReady = false;
            await CreateAsync();
        }
    }

    private void Reset()
    {
        ErrorText = null;
        StatusText = null;
        Card = null;
        Question = null;
        _plan = null;
        _result = null;
        _understanding = null;
        _openQuestion = null;
        _answers = new AssistantAnswers();
        _remembered = false;
        _playWhenReady = false;
        _generation++;
    }

    // ---- Rückfrage ----------------------------------------------------------------------------------------------------

    private void ShowQuestion(AssistantQuestion question)
    {
        _openQuestion = question;
        Card = null;
        var choices = question.Choices.Select(c => new AssistantChoice(c.Label, c.Detail, () => AnswerAsync(c), c.FaceId, c.FaceLibraryId)).ToList();
        foreach (var choice in choices.Where(c => c.FaceId is not null))
        {
            choice.Image = _services.Faces?.TryGet(choice.FaceId!.Value);
        }

        RequestFaces(choices.Where(c => c.FaceId is not null && c.Image is null).Select(c => (c.FaceLibraryId ?? 0, c.FaceId!.Value)));
        Question = new AssistantQuestionCard(question.Title, question.Text, Note(), choices);
    }

    private string? Note()
    {
        if (_understanding?.Resolution.Plan is not { } plan)
        {
            return null;
        }

        var parts = new List<string>();
        if (plan.Time is { } time)
        {
            parts.Add("Zeitraum verstanden als: " + time.Description);
        }

        parts.Add("Ort: " + (plan.Terms.FirstOrDefault(t => t.Matches.Any(m => m.Kind is Services.Assistant.TermMatchKind.Place or Services.Assistant.TermMatchKind.Country)) is { } place ? place.Text : "egal"));
        return string.Join(" · ", parts);
    }

    /// <summary>Antwort wählen (Personen merkt sich Lumora), dann neu auflösen.</summary>
    public async Task AnswerAsync(QuestionChoice choice)
    {
        if (_openQuestion is not { } question || _understanding is not { } understanding)
        {
            return;
        }

        if (choice.Value == "settings")
        {
            _services.Navigation.NavigateTo(NavigationTags.SettingsStories);
            return;
        }

        var generation = _generation;
        try
        {
            IsBusy = true;
            await _assistant.AnswerAsync(question, choice, _answers);
            var next = await _assistant.ResolveAsync(understanding.Sentence, understanding.Intent, _answers, understanding.FromRules);
            if (generation != _generation)
            {
                return;
            }

            await ApplyAsync(next, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorText = "Die Antwort konnte nicht übernommen werden: " + ex.Message;
            _logger.LogWarning(ex, "Assistent: Antwort nicht übernommen");
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    // ---- Karte --------------------------------------------------------------------------------------------------------

    private void BuildCard()
    {
        if (_plan is not { } plan || _result is not { } result)
        {
            return;
        }

        var facts = new List<AssistantFact>();
        var title = _assistant.BuildTitle(plan, result);

        // Personen
        if (plan.Persons.Count > 0)
        {
            var tokens = plan.Persons.Select(p => PersonToken(plan, p)).ToList();
            if (plan.Persons.Count > 1)
            {
                var match = new AssistantToken(plan.PersonMatch == IntentMatch.All ? "alle zusammen auf dem Foto" : "mindestens eine davon");
                match.Options.Add(Option("alle zusammen auf dem Foto", plan.PersonMatch == IntentMatch.All, p => p with { PersonMatch = IntentMatch.All }));
                match.Options.Add(Option("mindestens eine davon", plan.PersonMatch == IntentMatch.Any, p => p with { PersonMatch = IntentMatch.Any }));
                tokens.Add(match);
            }

            facts.Add(new AssistantFact(plan.Persons.Count == 1 ? "Person" : "Personen", tokens.ToArray()));
            var others = new AssistantToken(plan.Others == IntentOthers.Any ? "egal" : "nur diese Personen");
            others.Options.Add(Option("egal", plan.Others == IntentOthers.Any, p => p with { Others = IntentOthers.Any }));
            others.Options.Add(Option("nur diese Personen", plan.Others == IntentOthers.None, p => p with { Others = IntentOthers.None }));
            facts.Add(new AssistantFact("Weitere Personen", others));
        }

        // Zeitraum
        var timeToken = plan.Time is { } time ? new AssistantToken(time.Description, time.Source) : new AssistantToken("alle Jahre");
        if (plan.Time is not null)
        {
            timeToken.Options.Add(Option("Ohne Zeitraum", false, p => p with { Time = null }));
        }

        timeToken.Options.Add(new AssistantOption("Zeitraum ändern…", ChangeTimeAsync));
        facts.Add(new AssistantFact("Zeitraum", timeToken));

        // Urlaub / Reise
        if (plan.Trip is { } trip)
        {
            var tokens = new List<AssistantToken>();
            var detection = new AssistantToken(trip.UseDetection ? $"Reise-Erkennung ({trip.DetectionCount:N0})" : "Reise-Erkennung aus",
                trip.UseDetection ? "mehrere Tage weg von zu Hause" : null, isWarning: !trip.UseDetection && trip.Extra.Count == 0);
            if (trip.UseDetection)
            {
                detection.Options.Add(Option("Nicht verwenden", false, p => p with { Trip = trip with { UseDetection = false } }));
                if (trip.Extra.Count > 0)
                {
                    detection.Options.Add(Option("Nur die Reise-Erkennung", false, p => p with { Trip = trip with { Extra = trip.Extra.Select(m => m with { Selected = false }).ToList() } }));
                }
            }

            tokens.Add(detection);
            for (var i = 0; i < trip.Extra.Count; i++)
            {
                var index = i;
                var m = trip.Extra[i];
                var token = new AssistantToken($"{m.KindLabel} „{m.Label}“ ({m.Count:N0})", m.Selected ? null : "nicht verwendet");
                token.Options.Add(Option(m.Selected ? "Nicht verwenden" : "Verwenden", false, p => p with { Trip = trip with { Extra = Replace(trip.Extra, index, m with { Selected = !m.Selected }) } }));
                token.Options.Add(Option("Nur diese", false, p => p with { Trip = trip with { UseDetection = false, Extra = trip.Extra.Select((x, j) => x with { Selected = j == index }).ToList() } }));
                tokens.Add(token);
            }

            tokens[0].Options.Add(Option("Ohne Urlaub", false, p => p with { Trip = null }));
            facts.Add(new AssistantFact("Urlaub", tokens.ToArray()));
        }

        // Begriffe
        if (plan.Terms.Count > 0)
        {
            var tokens = new List<AssistantToken>();
            for (var i = 0; i < plan.Terms.Count; i++)
            {
                tokens.Add(TermToken(plan, i));
            }

            if (plan.Terms.Count(t => t.IsActive) > 1)
            {
                var match = new AssistantToken(plan.TermMatch == IntentMatch.All ? "alle Begriffe zusammen" : "einer reicht");
                match.Options.Add(Option("alle Begriffe zusammen", plan.TermMatch == IntentMatch.All, p => p with { TermMatch = IntentMatch.All }));
                match.Options.Add(Option("einer reicht", plan.TermMatch == IntentMatch.Any, p => p with { TermMatch = IntentMatch.Any }));
                tokens.Add(match);
            }

            facts.Add(new AssistantFact("Begriffe", tokens.ToArray()));
        }

        // Medien
        var media = new AssistantToken(plan.Media switch { IntentMedia.Photos => "nur Fotos", IntentMedia.Videos => "nur Videos", _ => "Fotos und Videos" });
        media.Options.Add(Option("Fotos und Videos", plan.Media == IntentMedia.All, p => p with { Media = IntentMedia.All }));
        media.Options.Add(Option("nur Fotos", plan.Media == IntentMedia.Photos, p => p with { Media = IntentMedia.Photos }));
        media.Options.Add(Option("nur Videos", plan.Media == IntentMedia.Videos, p => p with { Media = IntentMedia.Videos }));
        facts.Add(new AssistantFact("Medien", media));

        if (plan.MinRating > 0 || plan.FavoritesOnly)
        {
            var rating = new AssistantToken(plan.FavoritesOnly ? "nur Favoriten" : $"ab {plan.MinRating} Sternen");
            rating.Options.Add(Option("Entfernen", false, p => p with { MinRating = 0, FavoritesOnly = false }));
            facts.Add(new AssistantFact("Bewertung", rating));
        }

        // Auswahl („die besten N“)
        var best = new AssistantToken(plan.Best is { } n && n < result.Count ? $"die besten {result.Selected.Count:N0}" : "alle", plan.BestSuggested && plan.Best is not null ? "Vorschlag von Lumora" : null);
        best.Options.Add(Option("alle", plan.Best is null, p => p with { Best = null, BestSuggested = false }));
        foreach (var count in new[] { 24, 50, 100, 200 }.Append(plan.Best ?? 0).Where(c => c > 0).Distinct().Order())
        {
            best.Options.Add(Option($"die besten {count}", plan.Best == count, p => p with { Best = count, BestSuggested = false }));
        }

        facts.Add(new AssistantFact("Auswahl", best));

        // Was entsteht
        var quoted = title.Length > 0 ? $" „{title}“" : string.Empty;
        var action = new AssistantToken(plan.Action switch
        {
            IntentAction.Album => "Album" + quoted,
            IntentAction.Slideshow => "Diashow",
            IntentAction.Gallery => "Galerie mit diesen Fotos",
            IntentAction.Collage => "Collage-Bild" + quoted,
            _ => "Story" + quoted,
        });
        foreach (var (text, value) in new[] { ("Story", IntentAction.Story), ("Collage-Bild", IntentAction.Collage), ("Album", IntentAction.Album), ("Diashow", IntentAction.Slideshow), ("Nur in der Galerie zeigen", IntentAction.Gallery) })
        {
            action.Options.Add(Option(text, plan.Action == value, p => p with
            {
                Action = value,
                AlsoStory = value == IntentAction.Collage,
                Media = value == IntentAction.Collage ? IntentMedia.Photos : p.Media,
            }));
        }

        var actionTokens = new List<AssistantToken> { action };
        if (plan.Action == IntentAction.Collage)
        {
            var story = new AssistantToken(plan.AlsoStory ? "+ Story" + quoted : "ohne Story");
            story.Options.Add(Option("auch als Story speichern", plan.AlsoStory, p => p with { AlsoStory = true }));
            story.Options.Add(Option("nur das Collage-Bild", !plan.AlsoStory, p => p with { AlsoStory = false }));
            actionTokens.Add(story);
        }

        facts.Add(new AssistantFact("Was entsteht", actionTokens.ToArray()));

        // Ergebnis
        var what = plan.Media switch { IntentMedia.Photos => "Fotos", IntentMedia.Videos => "Videos", _ => "Fotos und Videos" };
        var resultText = result.Count == 0
            ? $"Keine {what} gefunden — ändern Sie einen Punkt oder den Satz."
            : $"{what} gefunden"
              + (plan.Best is { } k && k < result.Count ? $" · davon die besten {result.Selected.Count:N0}{(plan.Action == IntentAction.Collage ? " für die Collage" : string.Empty)} (Schärfe, Gesichter, keine Doppelten)" : string.Empty)
              + (result.SimilarSkipped > 0 ? $" · {result.SimilarSkipped:N0} ähnliche weggelassen" : string.Empty);
        var notes = new List<string>();
        if (_understanding?.FromRules == true)
        {
            notes.Add("Ohne Sprach-KI verstanden (einfache Regeln) — bitte prüfen.");
        }

        if (!plan.HasFilter)
        {
            notes.Add("Ohne Einschränkung sind das alle Fotos dieser Bibliothek.");
        }

        if (plan.Terms.Any(t => t.NotFound))
        {
            notes.Add("Nicht gefundene Begriffe wirken nicht — bitte ändern oder entfernen.");
        }

        if (plan.Trip is { UseDetection: false, Extra.Count: 0 })
        {
            notes.Add("Urlaub wird ohne Zuhause nicht erkannt — Zuhause unter Einstellungen › Storys festlegen.");
        }

        var preview = result.PreviewIds.Select(id => new AssistantPreviewImage(id) { Image = _services.Images.TryGet(id) }).ToList();
        var createText = plan.Action switch
        {
            IntentAction.Slideshow => "Diashow starten",
            IntentAction.Gallery => "In der Galerie zeigen",
            _ => "Erstellen",
        };
        Card = new AssistantCard(facts, result.Count.ToString("N0", German), resultText, preview, notes, result.Count > 0, createText);
        RequestFaces(plan.Persons.Where(p => p.FaceId is not null && _services.Faces?.TryGet(p.FaceId.Value) is null).Select(p => (p.FaceLibraryId ?? 0, (long)p.FaceId!.Value)));
        _ = RequestPreviewAsync(preview.Where(p => p.Image is null).Select(p => p.FileId).ToList());
    }

    private AssistantToken PersonToken(AssistantPlan plan, AssistantPerson person)
    {
        var token = new AssistantToken(person.Name, person.Relation, isPerson: true)
        {
            FaceId = person.FaceId,
            Image = person.FaceId is { } f ? _services.Faces?.TryGet(f) : null,
        };
        token.Options.Add(new AssistantOption("Andere Person…", () => ChoosePersonAsync(person)));
        token.Options.Add(Option("Entfernen", false, p => p with { Persons = p.Persons.Where(x => x.Id != person.Id).ToList() }));
        return token;
    }

    private AssistantToken TermToken(AssistantPlan plan, int index)
    {
        var term = plan.Terms[index];
        if (term.NotFound)
        {
            var missing = new AssistantToken(term.Text, "nicht gefunden", isWarning: true);
            missing.Options.Add(new AssistantOption("Begriff ändern…", () => ChangeTermAsync(index)));
            missing.Options.Add(Option("Entfernen", false, p => p with { Terms = p.Terms.Where((_, i) => i != index).ToList() }));
            return missing;
        }

        var detail = string.Join(" · ", term.Matches.Select(m => m.Selected ? m.Text : m.Text + " aus"));
        var token = new AssistantToken(term.Text, detail);
        if (term.Matches.Count > 1)
        {
            token.Options.Add(Option("Alle vereinigt (oder)", term.Matches.All(m => m.Selected), p => WithTerm(p, index, term with { Matches = term.Matches.Select(m => m with { Selected = true }).ToList() })));
            for (var j = 0; j < term.Matches.Count; j++)
            {
                var only = j;
                var match = term.Matches[j];
                token.Options.Add(Option($"Nur {match.Text}: {match.Label}", term.Matches.Count(m => m.Selected) == 1 && match.Selected,
                    p => WithTerm(p, index, term with { Matches = term.Matches.Select((m, k) => m with { Selected = k == only }).ToList() })));
            }
        }

        token.Options.Add(new AssistantOption("Begriff ändern…", () => ChangeTermAsync(index)));
        token.Options.Add(Option("Entfernen", false, p => p with { Terms = p.Terms.Where((_, i) => i != index).ToList() }));
        return token;
    }

    private static AssistantPlan WithTerm(AssistantPlan plan, int index, PlanTerm term)
        => plan with { Terms = plan.Terms.Select((t, i) => i == index ? term : t).ToList() };

    private static IReadOnlyList<T> Replace<T>(IReadOnlyList<T> list, int index, T value) => list.Select((x, i) => i == index ? value : x).ToList();

    private AssistantOption Option(string text, bool isChecked, Func<AssistantPlan, AssistantPlan> change)
        => new(text, () => ChangePlanAsync(change), isChecked);

    /// <summary>Punkt der Karte geändert: neu zählen (ohne Sprach-KI).</summary>
    public async Task ChangePlanAsync(Func<AssistantPlan, AssistantPlan> change)
    {
        if (_plan is null)
        {
            return;
        }

        _plan = change(_plan);
        await RunAsync(() => EvaluateAsync());
    }

    private async Task ChoosePersonAsync(AssistantPerson current)
    {
        try
        {
            _persons ??= await _assistant.GetPersonsAsync();
            var name = await _services.Dialogs.PromptTextAsync("Andere Person", "Name der Person (wie unter Personen):", string.Empty, "Übernehmen");
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var found = _persons.Where(p => p.NameMatches(name)).ToList();
            if (found.Count != 1)
            {
                ErrorText = found.Count == 0 ? $"„{name.Trim()}“ gibt es unter Personen nicht." : $"„{name.Trim()}“ passt auf mehrere Personen — bitte genauer.";
                return;
            }

            await ChangePlanAsync(p => p with { Persons = p.Persons.Select(x => x.Id == current.Id ? found[0] : x).DistinctBy(x => x.Id).ToList() });
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private async Task ChangeTimeAsync()
    {
        var text = await _services.Dialogs.PromptTextAsync("Zeitraum ändern", "Jahr oder Zeitraum, z. B. „2022“ oder „1985–2005“:", string.Empty, "Übernehmen");
        if (text is null)
        {
            return;
        }

        if (!TryParseYears(text, out var from, out var to))
        {
            ErrorText = "Bitte ein Jahr („2022“) oder einen Zeitraum („1985–2005“) eingeben.";
            return;
        }

        var description = StoryIntentResolver.Describe(from, to, null, null);
        await ChangePlanAsync(p => p with { Time = new PlanTime(from, to, null, null, description, "von Hand geändert") });
    }

    /// <summary>„2022“, „1985–2005“, „1985 - 2005“, „1985 bis 2005“.</summary>
    public static bool TryParseYears(string text, out DateOnly from, out DateOnly to)
    {
        from = to = default;
        var parts = text.Replace("bis", "-", StringComparison.OrdinalIgnoreCase).Replace('–', '-').Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a))
        {
            return false;
        }

        var b = a;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
        {
            return false;
        }

        if (a is < 1800 or > 2200 || b is < 1800 or > 2200)
        {
            return false;
        }

        from = new DateOnly(Math.Min(a, b), 1, 1);
        to = new DateOnly(Math.Max(a, b), 12, 31);
        return true;
    }

    private async Task ChangeTermAsync(int index)
    {
        if (_plan is null || index >= _plan.Terms.Count)
        {
            return;
        }

        var text = await _services.Dialogs.PromptTextAsync("Begriff ändern", "Tag, Kategorie, Ort, Motiv oder Person:", _plan.Terms[index].Text, "Übernehmen");
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var generation = _generation;
        await RunAsync(async () =>
        {
            var term = await _assistant.MatchTermAsync(text.Trim());
            if (generation != _generation || _plan is null)
            {
                return;
            }

            _plan = WithTerm(_plan, index, term);
            await EvaluateAsync();
        });
    }

    // ---- Aktionen -----------------------------------------------------------------------------------------------------

    /// <summary>„Erstellen“: erst jetzt entsteht etwas (Story, Album, Diashow, Galerie, Collage nach Dialog).</summary>
    [RelayCommand]
    public async Task CreateAsync()
    {
        if (_plan is not { } plan || _result is not { Count: > 0 } result)
        {
            return;
        }

        var ids = result.Selected.Count > 0 ? result.Selected : result.FileIds;
        var title = _assistant.BuildTitle(plan, result);
        await RunAsync(async () =>
        {
            switch (plan.Action)
            {
                case IntentAction.Slideshow:
                    if (_services.Slideshow is null)
                    {
                        ErrorText = "Die Diashow ist nicht verfügbar.";
                        return;
                    }

                    _services.Slideshow.Start(ids, 0, title.Length == 0 ? null : title);
                    StatusText = $"Diashow mit {ids.Count:N0} Fotos gestartet.";
                    break;
                case IntentAction.Gallery:
                    ShowGallery();
                    break;
                case IntentAction.Album:
                    var album = await _assistant.CreateAlbumAsync(plan, result);
                    StatusText = $"Album „{album.Name}“ mit {ids.Count:N0} Fotos angelegt (Seite „Alben“).";
                    Card = null;
                    break;
                case IntentAction.Collage:
                    if (_collage is null)
                    {
                        ErrorText = "Der Collage-Dialog ist nicht verfügbar.";
                        return;
                    }

                    int? storyId = plan.AlsoStory ? await _assistant.CreateStoryAsync(plan, result) : null;

                    var saved = await _collage.ShowAsync(new CollageRequest(title.Length == 0 ? null : title, ids, plan.Best));
                    StatusText = saved is null
                        ? storyId is null ? "Collage abgebrochen." : "Story angelegt, Collage abgebrochen."
                        : $"Collage gespeichert: {System.IO.Path.GetFileName(saved.Path)}{(storyId is null ? string.Empty : " · Story angelegt")}.";
                    Card = null;
                    break;
                default:
                    var id = await _assistant.CreateStoryAsync(plan, result);
                    StatusText = "Story angelegt.";
                    Card = null;
                    if (OpenStory is not null)
                    {
                        await OpenStory(id);
                    }

                    break;
            }
        });
    }

    /// <summary>„Nur Fotos zeigen (Galerie)“: Galerie mit genau den Treffern (Datei-Filter, ADR-0060).</summary>
    [RelayCommand]
    public void ShowGallery()
    {
        if (_result is not { Count: > 0 } result)
        {
            return;
        }

        if (_services.Gallery is null)
        {
            ErrorText = "Die Galerie ist nicht verfügbar.";
            return;
        }

        _services.Gallery.ShowInGallery(result.Selected.Count > 0 ? result.Selected : result.FileIds);
    }

    /// <summary>„Satz ändern“: Karte zu, Eingabe behalten und fokussieren.</summary>
    [RelayCommand]
    public void EditSentence()
    {
        Reset();
        FocusRequested?.Invoke();
    }

    /// <summary>„Abbrechen“: Karte bzw. Rückfrage verwerfen.</summary>
    [RelayCommand]
    public void Dismiss()
    {
        CancelAsk();
        Reset();
        Sentence = string.Empty;
        OnPropertyChanged(nameof(ShowHistory));
    }

    [RelayCommand]
    private void OpenSettings() => _services.Navigation.NavigateTo(NavigationTags.SettingsAssistant);

    // ---- Verlauf ------------------------------------------------------------------------------------------------------

    /// <summary>„Öffnen“ bzw. „Wieder abspielen“: aus der gespeicherten Absicht neu auflösen — ohne Sprach-KI.</summary>
    [RelayCommand]
    public async Task OpenHistoryAsync(AssistantHistoryRow? row)
    {
        if (row is null)
        {
            return;
        }

        var parsed = StoryIntentJson.Parse(row.IntentJson, DateOnly.FromDateTime(DateTime.Today));
        if (parsed.Intent is null)
        {
            ErrorText = "Dieser Eintrag lässt sich nicht mehr öffnen.";
            return;
        }

        Reset();
        Sentence = row.Sentence;
        _playWhenReady = row.IsSlideshow;
        _remembered = false;
        var generation = _generation;
        await RunAsync(async () =>
        {
            var understanding = await _assistant.ResolveAsync(row.Sentence, parsed.Intent, _answers);
            if (generation != _generation)
            {
                return;
            }

            await ApplyAsync(understanding, CancellationToken.None);
        });
    }

    [RelayCommand]
    public async Task DeleteHistoryAsync(AssistantHistoryRow? row)
    {
        if (row is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _assistant.DeleteHistoryAsync(row.Id);
            await LoadHistoryAsync();
        });
    }

    // ---- Bilder -------------------------------------------------------------------------------------------------------

    private async Task RequestPreviewAsync(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            var media = await _services.Media.LoadAsync(ids);
            _services.Images.Request(media.Values);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Assistent: Vorschaubilder nicht geladen");
        }
    }

    private void RequestFaces(IEnumerable<(int LibraryId, long FaceId)> faces)
    {
        if (_services.Faces is not { } source)
        {
            return;
        }

        foreach (var group in faces.Where(f => f.LibraryId > 0).GroupBy(f => f.LibraryId))
        {
            source.Request(group.Key, group.Select(f => f.FaceId).Distinct().ToList());
        }
    }

    private void OnImagesReady(IReadOnlyList<int> fileIds)
    {
        if (Card is not { } card)
        {
            return;
        }

        var ready = fileIds.ToHashSet();
        foreach (var image in card.Preview.Where(p => ready.Contains(p.FileId)))
        {
            image.Image = _services.Images.TryGet(image.FileId);
        }
    }

    private void OnFacesReady(IReadOnlyList<(long FaceId, ImageSource Image)> batch)
    {
        var map = new Dictionary<long, ImageSource>();
        foreach (var (faceId, image) in batch)
        {
            map[faceId] = image;
        }

        foreach (var choice in Question?.Choices ?? [])
        {
            if (choice.FaceId is { } f && map.TryGetValue(f, out var image))
            {
                choice.Image = image;
            }
        }

        foreach (var token in Card?.Facts.SelectMany(fact => fact.Tokens) ?? [])
        {
            if (token.FaceId is { } f && map.TryGetValue(f, out var image))
            {
                token.Image = image;
            }
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            ErrorText = null;
            IsBusy = true;
            await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or AssistantException)
        {
            ErrorText = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorText = "Das hat nicht geklappt: " + ex.Message;
            _logger.LogError(ex, "Assistent: Aktion fehlgeschlagen");
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    public void Dispose()
    {
        CancelAsk();
        _services.Images.ImagesReady -= OnImagesReady;
        if (_services.Faces is not null)
        {
            _services.Faces.ImagesReady -= OnFacesReady;
        }

        _messenger.UnregisterAll(this);
    }
}
