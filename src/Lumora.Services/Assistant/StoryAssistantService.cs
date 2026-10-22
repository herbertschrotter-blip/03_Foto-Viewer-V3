using System.Globalization;
using Microsoft.Extensions.Logging;
using Lumora.Services.Albums;
using Lumora.Services.Library;
using Lumora.Services.Quality;
using Lumora.Services.Stories;

namespace Lumora.Services.Assistant;

/// <summary>Verständnis eines Satzes: Absicht, Auflösung, Herkunft (Sprach-KI oder Regeln).</summary>
public sealed record AssistantUnderstanding(string Sentence, StoryIntent Intent, AssistantResolution Resolution, bool FromRules);

/// <summary>Satz nicht verstanden bzw. Assistent nicht verfügbar — Text für den Benutzer.</summary>
public sealed class AssistantException(string message, bool needsSetup = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Ollama oder Modell fehlt — die Leiste zeigt den Weg in Einstellungen › Assistent.</summary>
    public bool NeedsSetup { get; } = needsSetup;
}

/// <summary>
/// Story-Assistent (ADR-0063): Satz → Sprach-KI (nur Absicht) → Auflösung in Lumora → Treffer → nach Bestätigung Story,
/// Album, Diashow, Galerie oder Collage. Alles im aktuellen Bereich (<see cref="ILibraryScope"/>), Verlauf und gemerkte
/// Antworten im Katalog der Sitzung. Protokolle nennen nie den Satz, nur Zahlen.
/// </summary>
public interface IStoryAssistant
{
    Task<AssistantSettings> GetSettingsAsync(CancellationToken ct = default);

    Task SaveSettingsAsync(AssistantSettings settings, CancellationToken ct = default);

    /// <summary>Satz verstehen: Sprach-KI (Rückfall: Regeln für klare Sätze), dann auflösen. Wirft <see cref="AssistantException"/>.</summary>
    Task<AssistantUnderstanding> UnderstandAsync(string sentence, CancellationToken ct = default);

    /// <summary>Gespeicherte bzw. geänderte Absicht erneut auflösen (Verlauf „Öffnen“, nach einer Antwort) — ohne Sprach-KI.</summary>
    Task<AssistantUnderstanding> ResolveAsync(string sentence, StoryIntent intent, AssistantAnswers answers, bool fromRules = false, CancellationToken ct = default);

    /// <summary>Antwort auf eine Rückfrage: Personen merkt sich Lumora als Alias, ein Zuhause in Einstellungen › Storys.</summary>
    Task AnswerAsync(AssistantQuestion question, QuestionChoice choice, AssistantAnswers answers, CancellationToken ct = default);

    /// <summary>Treffer zählen, „die besten N“ wählen, Vorschau — nur Lumora-Abfragen, keine Sprach-KI.</summary>
    Task<AssistantResult> EvaluateAsync(AssistantPlan plan, CancellationToken ct = default);

    /// <summary>Lumoras Vorschlag für „die schönsten“ ohne Zahl (null = alle).</summary>
    int? SuggestBest(int count);

    /// <summary>Sachliche Überschrift: „Nicole und Nina · Urlaub 2021–2026“, „Anna · 1985–2005“ — kein erfundener Text.</summary>
    string BuildTitle(AssistantPlan plan, AssistantResult result);

    /// <summary>Eigene Story aus den gewählten Treffern (sachliche Überschrift, änderbar); liefert die Id.</summary>
    Task<int> CreateStoryAsync(AssistantPlan plan, AssistantResult result, CancellationToken ct = default);

    /// <summary>Neues eigenes Album (Name = Überschrift, eindeutig gemacht) mit den gewählten Treffern.</summary>
    Task<AlbumInfo> CreateAlbumAsync(AssistantPlan plan, AssistantResult result, CancellationToken ct = default);

    Task<IReadOnlyList<AssistantHistoryItem>> GetHistoryAsync(CancellationToken ct = default);

    /// <summary>Satz im Verlauf merken (nur wenn „Zuletzt gefragte Sätze merken“ an ist).</summary>
    Task RememberAsync(string sentence, StoryIntent intent, IntentAction action, int resultCount, CancellationToken ct = default);

    Task DeleteHistoryAsync(int id, CancellationToken ct = default);

    Task<int> ClearHistoryAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AssistantAliasInfo>> GetAliasesAsync(CancellationToken ct = default);

    Task DeleteAliasAsync(string term, CancellationToken ct = default);

    /// <summary>Personen des Bereichs (Auswahl „Person ändern“ in der Karte).</summary>
    Task<IReadOnlyList<AssistantPerson>> GetPersonsAsync(CancellationToken ct = default);

    /// <summary>Begriff neu zuordnen (Karte: Begriff ändern).</summary>
    Task<PlanTerm> MatchTermAsync(string text, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class StoryAssistantService : IStoryAssistant
{
    /// <summary>Höchstens so viele Treffer gehen in die Auswahl „die besten N“ (gleichmäßig über die Zeit verteilt).</summary>
    public const int MaxHighlightInput = 4000;

    public const int PreviewCount = 8;

    private readonly IIntentModel _model;
    private readonly IAssistantStore _store;
    private readonly IAssistantSettingsStore _settings;
    private readonly AssistantQueries _queries;
    private readonly StoryIntentResolver _resolver;
    private readonly ILibraryScope _scope;
    private readonly IHighlightSelector _selector;
    private readonly IStoryService? _stories;
    private readonly IAlbumService? _albums;
    private readonly IStorySettingsStore? _storySettings;
    private readonly StoryClock _clock;
    private readonly ILogger<StoryAssistantService> _logger;

    public StoryAssistantService(
        IIntentModel model,
        IAssistantStore store,
        IAssistantSettingsStore settings,
        AssistantQueries queries,
        StoryIntentResolver resolver,
        ILibraryScope scope,
        IHighlightSelector selector,
        ILogger<StoryAssistantService> logger,
        IStoryService? stories = null,
        IAlbumService? albums = null,
        IStorySettingsStore? storySettings = null,
        StoryClock? clock = null)
    {
        _model = model;
        _store = store;
        _settings = settings;
        _queries = queries;
        _resolver = resolver;
        _scope = scope;
        _selector = selector;
        _logger = logger;
        _stories = stories;
        _albums = albums;
        _storySettings = storySettings;
        _clock = clock ?? new StoryClock();
    }

    public Task<AssistantSettings> GetSettingsAsync(CancellationToken ct = default) => _settings.LoadAsync(ct);

    public Task SaveSettingsAsync(AssistantSettings settings, CancellationToken ct = default) => _settings.SaveAsync(settings, ct);

    public async Task<AssistantUnderstanding> UnderstandAsync(string sentence, CancellationToken ct = default)
    {
        var text = (sentence ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new AssistantException("Bitte einen Satz eingeben.");
        }

        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        if (!scope.HasLibrary)
        {
            throw new AssistantException("Ohne Bibliothek gibt es nichts zu suchen.");
        }

        var vocabulary = await VocabularyAsync(scope, ct, fresh: true).ConfigureAwait(false);
        var prompt = vocabulary.ToPrompt(text, _clock.Today);
        StoryIntent intent;
        var fromRules = false;
        try
        {
            var response = await _model.InterpretAsync(prompt, ct).ConfigureAwait(false);
            var parsed = StoryIntentJson.Parse(response.Json, _clock.Today, response.CutOff);
            if (parsed.Intent is null)
            {
                _logger.LogWarning("Assistent: Antwort der Sprach-KI ungültig (abgeschnitten: {Truncated})", parsed.Truncated);
                intent = SimpleIntentParser.TryParse(prompt) ?? throw new AssistantException(parsed.Error ?? "Den Satz habe ich nicht verstanden.");
                fromRules = true;
            }
            else
            {
                intent = parsed.Intent;
            }
        }
        catch (IntentModelException ex)
        {
            // Ohne Ollama: klare Sätze (Name + Jahr …) regelbasiert, sonst der Weg in die Einstellungen
            intent = SimpleIntentParser.TryParse(prompt) ?? throw new AssistantException(
                ex.Message + (ex.Failure is IntentModelFailure.ServerUnreachable or IntentModelFailure.ModelMissing
                    ? " Einfache Sätze wie „Anna 2019“ verstehe ich auch ohne."
                    : string.Empty),
                ex.Failure is IntentModelFailure.ServerUnreachable or IntentModelFailure.ModelMissing, ex);
            fromRules = true;
        }

        return await ResolveCoreAsync(text, intent, scope, vocabulary, new AssistantAnswers(), fromRules, ct).ConfigureAwait(false);
    }

    public async Task<AssistantUnderstanding> ResolveAsync(string sentence, StoryIntent intent, AssistantAnswers answers, bool fromRules = false, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var vocabulary = await VocabularyAsync(scope, ct).ConfigureAwait(false);
        return await ResolveCoreAsync(sentence, intent, scope, vocabulary, answers, fromRules, ct).ConfigureAwait(false);
    }

    private async Task<AssistantUnderstanding> ResolveCoreAsync(string sentence, StoryIntent intent, LibraryScopeInfo scope, AssistantVocabulary vocabulary, AssistantAnswers answers, bool fromRules, CancellationToken ct)
    {
        var resolution = await _resolver.ResolveAsync(intent, scope, vocabulary, _clock.Today, answers, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Assistent: Anfrage verstanden ({Source}) — {Persons} Personen, {Terms} Begriffe, {Questions} Rückfragen",
            fromRules ? "Regeln" : "Sprach-KI", resolution.Plan.Persons.Count, resolution.Plan.Terms.Count, resolution.Questions.Count);
        return new AssistantUnderstanding(sentence, intent, resolution, fromRules);
    }

    public async Task AnswerAsync(AssistantQuestion question, QuestionChoice choice, AssistantAnswers answers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(answers);
        switch (question.Kind)
        {
            case QuestionKind.Person when choice.Value == "skip":
                answers.Skipped.Add("person:" + question.Key);
                break;
            case QuestionKind.Person:
                answers.Persons[question.Key] = choice.PersonIds;
                var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
                await _store.SetAliasAsync(scope.GroupId, question.Key, choice.PersonIds, ct).ConfigureAwait(false);
                _vocabulary = null;
                _logger.LogInformation("Assistent: Antwort auf Rückfrage gemerkt ({Count} Personen)", choice.PersonIds.Count);
                break;
            case QuestionKind.BirthDate:
                answers.Skipped.Add(question.Key);
                break;
            case QuestionKind.Home when choice.Value is { } value && (value.StartsWith("place:", StringComparison.Ordinal) || value.StartsWith("geo:", StringComparison.Ordinal)):
                if (_storySettings is not null)
                {
                    var settings = await _storySettings.LoadAsync(ct).ConfigureAwait(false);
                    await _storySettings.SaveAsync(settings with { Home = StoryHomeSetting.Parse(value) }, ct).ConfigureAwait(false);
                    _logger.LogInformation("Assistent: Zuhause aus der Rückfrage gespeichert");
                }

                break;
            case QuestionKind.Home:
                answers.Skipped.Add(StoryIntentResolver.HomeKey);
                break;
        }
    }

    public async Task<AssistantResult> EvaluateAsync(AssistantPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var files = await _queries.ReadFilesAsync(scope, plan, ct).ConfigureAwait(false);
        var ids = files.Select(f => f.FileId).ToList();
        var videos = files.Count(f => f.IsVideo);
        IReadOnlyList<int> selected = ids;
        IReadOnlyList<int> preview;
        var similar = 0;
        if (plan.Best is { } best && best > 0 && best < ids.Count)
        {
            var input = Sample(ids, MaxHighlightInput);
            var picks = await _selector.SelectAsync(input, new HighlightOptions(Math.Min(1, (double)best / input.Count), best, best), ct).ConfigureAwait(false);
            selected = picks.Select(p => p.FileId).ToList();
            similar = picks.Sum(p => p.SimilarCount);
            var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            preview = picks.OrderByDescending(p => p.Score).Take(PreviewCount).Select(p => p.FileId).OrderBy(id => order.GetValueOrDefault(id)).ToList();
        }
        else if (ids.Count <= PreviewCount)
        {
            preview = ids;
        }
        else
        {
            var input = Sample(ids, 1500);
            preview = (await _selector.SelectAsync(input, new HighlightOptions((double)PreviewCount / input.Count, PreviewCount, PreviewCount), ct).ConfigureAwait(false))
                .Select(p => p.FileId).ToList();
        }

        var chosen = selected.ToHashSet();
        var years = files.Where(f => chosen.Contains(f.FileId)).Select(f => DateOnly.FromDayNumber(EpochDay + f.Day).Year).ToList();
        _logger.LogInformation("Assistent: Anfrage, {Count} Treffer ({Selected} gewählt)", ids.Count, selected.Count);
        return new AssistantResult(ids, selected, preview, ids.Count - videos, videos, similar) { Years = years.Count == 0 ? null : (years.Min(), years.Max()) };
    }

    /// <summary>
    /// „Die schönsten“ ohne Zahl: bis 24 Treffer alle, sonst rund 10 % auf Fünfer gerundet, mindestens 24, höchstens 100 —
    /// ein Album/eine Story bleibt so überschaubar (ADR-0063).
    /// </summary>
    public int? SuggestBest(int count)
        => count <= 24 ? null : Math.Clamp((int)Math.Round(count * 0.10 / 5.0, MidpointRounding.AwayFromZero) * 5, 24, 100);

    public string BuildTitle(AssistantPlan plan, AssistantResult result) => AssistantTitleBuilder.Build(plan, result.Years);

    public async Task<int> CreateStoryAsync(AssistantPlan plan, AssistantResult result, CancellationToken ct = default)
    {
        if (_stories is null)
        {
            throw new InvalidOperationException("Storys sind nicht verfügbar.");
        }

        var ids = result.Selected.Count > 0 ? result.Selected : result.FileIds;
        if (ids.Count == 0)
        {
            throw new InvalidOperationException("Keine Fotos gefunden — es entsteht keine Story.");
        }

        var title = AssistantTitleBuilder.Build(plan, result.Years);
        var id = await _stories.CreateFromSelectionAsync(ids, title.Length == 0 ? null : title, ct).ConfigureAwait(false);
        _logger.LogInformation("Assistent: eigene Story {Id} angelegt ({Count} Dateien)", id, ids.Count);
        return id;
    }

    public async Task<AlbumInfo> CreateAlbumAsync(AssistantPlan plan, AssistantResult result, CancellationToken ct = default)
    {
        if (_albums is null)
        {
            throw new InvalidOperationException("Alben sind nicht verfügbar.");
        }

        var ids = result.Selected.Count > 0 ? result.Selected : result.FileIds;
        if (ids.Count == 0)
        {
            throw new InvalidOperationException("Keine Fotos gefunden — es entsteht kein Album.");
        }

        var title = AssistantTitleBuilder.Build(plan, result.Years);
        var name = (title.Length == 0 ? "Assistent" : title).Trim();
        if (name.Length > AlbumService.MaxNameLength - 6)
        {
            name = name[..(AlbumService.MaxNameLength - 6)].TrimEnd();
        }

        var candidate = name;
        for (var n = 2; await _albums.FindByNameAsync(candidate, ct).ConfigureAwait(false) is not null; n++)
        {
            candidate = $"{name} ({n.ToString(CultureInfo.InvariantCulture)})";
        }

        var album = await _albums.CreateAsync(candidate, ct).ConfigureAwait(false);
        await _albums.AddFilesAsync(album.Id, ids, ct).ConfigureAwait(false);
        _logger.LogInformation("Assistent: Album {Id} angelegt ({Count} Dateien)", album.Id, ids.Count);
        return await _albums.GetAsync(album.Id, ct).ConfigureAwait(false) ?? album;
    }

    private static readonly int EpochDay = DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

    public async Task<IReadOnlyList<AssistantHistoryItem>> GetHistoryAsync(CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return scope.HasLibrary ? await _store.GetHistoryAsync(scope.GroupId, 8, ct).ConfigureAwait(false) : [];
    }

    public async Task RememberAsync(string sentence, StoryIntent intent, IntentAction action, int resultCount, CancellationToken ct = default)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        if (!settings.RememberHistory || !scope.HasLibrary)
        {
            return;
        }

        await _store.AddHistoryAsync(scope.GroupId, sentence, intent, action, resultCount, ct).ConfigureAwait(false);
    }

    public Task DeleteHistoryAsync(int id, CancellationToken ct = default) => _store.DeleteHistoryAsync(id, ct);

    public async Task<int> ClearHistoryAsync(CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var count = await _store.ClearHistoryAsync(scope.GroupId, ct).ConfigureAwait(false);
        _logger.LogInformation("Assistent: Verlauf gelöscht ({Count} Sätze)", count);
        return count;
    }

    public async Task<IReadOnlyList<AssistantAliasInfo>> GetAliasesAsync(CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return await _store.GetAliasesAsync(scope.GroupId, ct).ConfigureAwait(false);
    }

    public async Task DeleteAliasAsync(string term, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        await _store.DeleteAliasAsync(scope.GroupId, term, ct).ConfigureAwait(false);
        _vocabulary = null;
    }

    public async Task<IReadOnlyList<AssistantPerson>> GetPersonsAsync(CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return (await VocabularyAsync(scope, ct).ConfigureAwait(false)).Persons;
    }

    public async Task<PlanTerm> MatchTermAsync(string text, CancellationToken ct = default)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        var vocabulary = await VocabularyAsync(scope, ct).ConfigureAwait(false);
        var resolution = await _resolver.ResolveAsync(new StoryIntent { Terms = [text] }, scope, vocabulary, _clock.Today, null, ct).ConfigureAwait(false);
        return resolution.Plan.Terms.FirstOrDefault() ?? new PlanTerm(text, []);
    }

    /// <summary>
    /// Wortschatz des Bereichs — frisch bei jedem neuen Satz, danach (Antworten, „Begriff ändern“, Verlauf) bis zu
    /// <see cref="VocabularyLifetime"/> aus dem Zwischenspeicher (die Personen-Zählung über Gesichter ist teuer).
    /// Gemerkte Antworten setzen ihn zurück.
    /// </summary>
    private async Task<AssistantVocabulary> VocabularyAsync(LibraryScopeInfo scope, CancellationToken ct, bool fresh = false)
    {
        var c = _vocabulary;
        if (!fresh && c is not null && c.Key == scope.Key && DateTime.UtcNow - c.At < VocabularyLifetime)
        {
            return c.Value;
        }

        var value = await _store.ReadVocabularyAsync(scope, ct).ConfigureAwait(false);
        _vocabulary = new VocabularyCache(scope.Key, value, DateTime.UtcNow);
        return value;
    }

    /// <summary>So lange gilt ein gelesener Wortschatz für Folgeschritte desselben Satzes.</summary>
    public static readonly TimeSpan VocabularyLifetime = TimeSpan.FromMinutes(2);

    private VocabularyCache? _vocabulary;

    private sealed record VocabularyCache(string Key, AssistantVocabulary Value, DateTime At);

    /// <summary>Gleichmäßige Stichprobe über die (zeitlich sortierte) Liste.</summary>
    internal static IReadOnlyList<int> Sample(IReadOnlyList<int> ids, int max)
    {
        if (ids.Count <= max)
        {
            return ids;
        }

        var list = new List<int>(max);
        for (var i = 0; i < max; i++)
        {
            list.Add(ids[(int)((long)i * ids.Count / max)]);
        }

        return list;
    }
}
