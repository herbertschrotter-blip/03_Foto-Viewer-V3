using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Assistant;
using Lumora.Services.Infrastructure;
using Lumora.Services.Places;
using Lumora.Services.Stories;
using Lumora.Tests.Stories;

namespace Lumora.Tests.Assistant;

/// <summary>Offline-Ortssuche ohne GeoNames-Daten: findet die Testorte nach Namen.</summary>
internal sealed class FakeGeocoder : IReverseGeocoder
{
    public PlaceInfo? Nearest(double latitude, double longitude) => null;

    public IReadOnlyList<(PlaceInfo Place, double Latitude, double Longitude)> Search(string text, int max)
        => TestPlaces.All.Where(p => p.Value.Name.StartsWith(text.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(p => (new PlaceInfo(p.Key, p.Value.Name, null, p.Value.Country[..2].ToUpperInvariant(), p.Value.Country), p.Value.Lat, p.Value.Lon))
            .Take(max).ToList();
}

/// <summary>Sprach-KI-Attrappe: liefert eine feste Antwort bzw. wirft; merkt sich die Anfragen (was die KI „sieht“).</summary>
internal sealed class ScriptedIntentModel : IIntentModel
{
    public List<IntentPrompt> Prompts { get; } = [];

    public Func<IntentPrompt, IntentModelResponse> Answer { get; set; } = _ => new IntentModelResponse("{}");

    public Task<IntentModelResponse> InterpretAsync(IntentPrompt prompt, CancellationToken ct)
    {
        Prompts.Add(prompt);
        return Task.FromResult(Answer(prompt));
    }

    public static ScriptedIntentModel Returning(StoryIntent intent) => new() { Answer = _ => new IntentModelResponse(StoryIntentJson.ToJson(intent)) };
}

/// <summary>Protokoll im Speicher (Prüfung: kein Satz im Protokoll).</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lines.Add(logLevel + ": " + formatter(state, exception));
}

/// <summary>Collage-Renderer-Attrappe: merkt den Auftrag, schreibt eine kleine Datei.</summary>
internal sealed class RecordingCollageRenderer : ICollageRenderer
{
    public List<CollageRenderJob> Jobs { get; } = [];

    public Task RenderAsync(CollageRenderJob job, IProgress<int>? progress, CancellationToken ct)
    {
        Jobs.Add(job);
        File.WriteAllBytes(job.OutputPath, [1, 2, 3]);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Assistent auf einem Story-Katalog (<see cref="StoryWorld"/>, Uhr 21.10.2026): Speicher, Abfragen, Auflösung, Dienst
/// mit Sprach-KI-Attrappe, Collage-Dienst mit Renderer-Attrappe. Synthetische Dateien, keine echten Fotos.
/// </summary>
internal sealed class AssistantWorld : IDisposable
{
    private AssistantWorld(StoryWorld w)
    {
        W = w;
        Store = new AssistantStore(w.Fx.ContextFactory);
        Settings = new AssistantSettingsStore(w.Fx.ContextFactory);
        Queries = new AssistantQueries(w.Fx.ContextFactory, w.Clock);
        Resolver = new StoryIntentResolver(Queries, new FakeGeocoder(), w.Places, w.Settings);
        Model = new ScriptedIntentModel();
        Logger = new ListLogger<StoryAssistantService>();
        Assistant = new StoryAssistantService(Model, Store, Settings, Queries, Resolver, w.Scope, w.Selector, Logger, w.Stories, w.Albums, w.Settings, w.Clock);
        Renderer = new RecordingCollageRenderer();
        Collage = new CollageService(w.Fx.ContextFactory, w.Scope, w.Selector, Renderer, NullLogger<CollageService>.Instance, w.Catalog, w.Clock);
    }

    public StoryWorld W { get; }

    public AssistantStore Store { get; }

    public AssistantSettingsStore Settings { get; }

    public AssistantQueries Queries { get; }

    public StoryIntentResolver Resolver { get; }

    public ScriptedIntentModel Model { get; }

    public ListLogger<StoryAssistantService> Logger { get; }

    public StoryAssistantService Assistant { get; }

    public RecordingCollageRenderer Renderer { get; }

    public CollageService Collage { get; }

    public static DateOnly Today => StoryWorld.Today;

    public static async Task<AssistantWorld> CreateAsync() => new(await StoryWorld.CreateAsync());

    public async Task<int> GroupAsync() => await W.GroupIdAsync();

    public async Task<AssistantVocabulary> VocabularyAsync() => await Store.ReadVocabularyAsync(await W.Scope.GetCurrentAsync());

    /// <summary>Person mit Details (Beziehung, Geburtsdatum, „ich“) und bestätigten Gesichtern auf den Dateien.</summary>
    public async Task<int> PersonAsync(string name, IEnumerable<int> files, string? relation = null, PersonBirthDate? birth = null, bool isMe = false, int? groupId = null)
    {
        var id = await W.AddPersonAsync(name, files, groupId: groupId);
        var group = groupId ?? await GroupAsync();
        int? relationId = null;
        if (relation is not null)
        {
            var existing = W.Scalar("SELECT COALESCE((SELECT Id FROM PersonRelations WHERE GroupId = $g AND Name = $n), 0)", ("$g", group), ("$n", relation));
            if (existing == 0)
            {
                W.Execute("INSERT INTO PersonRelations (Name, GroupId) VALUES ($n, $g)", ("$n", relation), ("$g", group));
                existing =W.Scalar("SELECT Id FROM PersonRelations WHERE GroupId = $g AND Name = $n", ("$g", group), ("$n", relation));
            }

            relationId = (int)existing;
        }

        W.Execute("UPDATE Persons SET RelationId = $r, BirthYear = $y, BirthMonth = $m, BirthDay = $d, IsMe = $me WHERE Id = $p",
            ("$r", (object?)relationId ?? DBNull.Value), ("$y", (object?)birth?.Year ?? DBNull.Value), ("$m", (object?)birth?.Month ?? DBNull.Value),
            ("$d", (object?)birth?.Day ?? DBNull.Value), ("$me", isMe), ("$p", id));
        return id;
    }

    /// <summary>Tag mit Dateien.</summary>
    public async Task<int> TagAsync(string name, IEnumerable<int> files, int? groupId = null)
    {
        var group = groupId ?? await GroupAsync();
        W.Execute("INSERT INTO Tags (Name, Color, SortOrder, CreatedAtUtc, GroupId) VALUES ($n, '#888888', 0, 0, $g)", ("$n", name), ("$g", group));
        var id = (int)W.Scalar("SELECT Id FROM Tags WHERE Name = $n AND GroupId = $g", ("$n", name), ("$g", group));
        foreach (var file in files)
        {
            W.Execute("INSERT INTO FileTags (FileId, TagId) VALUES ($f, $t)", ("$f", file), ("$t", id));
        }

        return id;
    }

    /// <summary>Kategorie mit einem Wert; die Dateien bekommen den Wert.</summary>
    public async Task<(int Category, int Value)> CategoryAsync(string category, string value, IEnumerable<int> files)
    {
        var group = await GroupAsync();
        W.Execute("INSERT INTO Categories (Name, SortOrder, CreatedAtUtc, GroupId) VALUES ($n, 0, 0, $g)", ("$n", category), ("$g", group));
        var c = (int)W.Scalar("SELECT Id FROM Categories WHERE Name = $n AND GroupId = $g", ("$n", category), ("$g", group));
        W.Execute("INSERT INTO CategoryValues (CategoryId, Name, SortOrder) VALUES ($c, $n, 0)", ("$c", c), ("$n", value));
        var v = (int)W.Scalar("SELECT Id FROM CategoryValues WHERE CategoryId = $c AND Name = $n", ("$c", c), ("$n", value));
        foreach (var file in files)
        {
            W.Execute("INSERT INTO FileCategoryValues (FileId, CategoryId, ValueId) VALUES ($f, $c, $v)", ("$f", file), ("$c", c), ("$v", v));
        }

        return (c, v);
    }

    /// <summary>Motiv-Begriff mit Treffern.</summary>
    public async Task<int> MotifAsync(string name, IEnumerable<int> files)
    {
        var group = await GroupAsync();
        W.Execute("INSERT INTO MotifTerms (LibraryId, GroupId, Name, SortOrder, CreatedAtUtc) VALUES ($l, $g, $n, 0, 0)", ("$l", W.LibraryId), ("$g", group), ("$n", name));
        var id = (int)W.Scalar("SELECT Id FROM MotifTerms WHERE Name = $n AND GroupId = $g", ("$n", name), ("$g", group));
        foreach (var file in files)
        {
            W.Execute("INSERT INTO FileMotifs (FileId, TermId, Score) VALUES ($f, $t, 0.5)", ("$f", file), ("$t", id));
        }

        return id;
    }

    /// <summary>Zuhause fest auf Graz (Reise-Erkennung ohne Rückfrage).</summary>
    public async Task SetHomeGrazAsync()
    {
        var settings = await W.Settings.LoadAsync();
        await W.Settings.SaveAsync(settings with { Home = new StoryHomeSetting(TestPlaces.Graz) });
    }

    public void Dispose() => W.Dispose();
}
