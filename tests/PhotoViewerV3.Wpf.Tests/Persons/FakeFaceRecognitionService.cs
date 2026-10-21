using System.Drawing;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Lightbox.Faces;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Wpf.Tests.Persons;

/// <summary>
/// Fake des Zuordnungsdienstes (P1-Vertrag): hält Gesichter und Personen im Speicher und setzt
/// Bestätigen/Ablehnen/Ignorieren so um, wie die Lightbox es erwartet. Implementiert das Interface nicht selbst, sondern
/// über <see cref="FaceRecognitionForwarder"/> (<see cref="AsService"/>) — so kompiliert er auch gegen P1s vollständiges
/// Interface (weitere Mitglieder).
/// </summary>
public sealed class FakeFaceRecognitionService
{
    private long _nextFaceId = 1;
    private int _nextPersonId = 1;

    public Dictionary<int, List<FacePersonInfo>> Faces { get; } = new();

    public List<PersonSummary> Persons { get; } = new();

    public List<string> Calls { get; } = new();

    public int Invalidations { get; private set; }

    public IFaceRecognitionService AsService() => FaceRecognitionForwarder.Create(this);

    public void InvalidateAll() => Invalidations++;

    public int AddPerson(string name, int faceCount = 0, bool hidden = false)
    {
        var id = _nextPersonId++;
        Persons.Add(new PersonSummary(id, name, faceCount, faceCount, null, hidden));
        return id;
    }

    public long AddFace(int fileId, FaceState state, int? personId = null, int? suggestedId = null, float x = 0.25f)
    {
        var id = _nextFaceId++;
        if (!Faces.TryGetValue(fileId, out var list))
        {
            Faces[fileId] = list = new List<FacePersonInfo>();
        }

        list.Add(new FacePersonInfo(id, new RectangleF(x, 0.2f, 0.2f, 0.3f), personId, NameOf(personId), state, suggestedId, NameOf(suggestedId), suggestedId is null ? null : 0.7f));
        return id;
    }

    public Task<IReadOnlyDictionary<int, IReadOnlyList<FacePersonInfo>>> GetFacesForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        Calls.Add("get");
        IReadOnlyDictionary<int, IReadOnlyList<FacePersonInfo>> result = fileIds
            .Where(Faces.ContainsKey)
            .ToDictionary(id => id, id => (IReadOnlyList<FacePersonInfo>)Faces[id].ToList());
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<PersonSummary>> GetPersonsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PersonSummary>>(Persons.ToList());

    public Task ConfirmAsync(long faceId, int personId)
    {
        Calls.Add($"confirm {faceId} {personId}");
        Update(faceId, f => f with { State = FaceState.Confirmed, PersonId = personId, PersonName = NameOf(personId), SuggestedPersonId = null, SuggestedPersonName = null, SuggestionScore = null });
        return Task.CompletedTask;
    }

    public Task ConfirmNewPersonAsync(long faceId, string name)
    {
        Calls.Add($"new {faceId} {name}");
        var id = AddPerson(name, 1);
        Update(faceId, f => f with { State = FaceState.Confirmed, PersonId = id, PersonName = name, SuggestedPersonId = null, SuggestedPersonName = null });
        return Task.CompletedTask;
    }

    public Task RejectSuggestionAsync(long faceId)
    {
        Calls.Add($"reject {faceId}");
        Update(faceId, f => f with { State = FaceState.Unconfirmed, SuggestedPersonId = null, SuggestedPersonName = null, SuggestionScore = null });
        return Task.CompletedTask;
    }

    public Task IgnoreFaceAsync(long faceId)
    {
        Calls.Add($"ignore {faceId}");
        Update(faceId, f => f with { State = FaceState.Ignored });
        return Task.CompletedTask;
    }

    private string? NameOf(int? personId) => personId is { } id ? Persons.FirstOrDefault(p => p.Id == id)?.Name : null;

    private void Update(long faceId, Func<FacePersonInfo, FacePersonInfo> change)
    {
        foreach (var list in Faces.Values)
        {
            var index = list.FindIndex(f => f.FaceId == faceId);
            if (index >= 0)
            {
                list[index] = change(list[index]);
            }
        }
    }
}

/// <summary>Dialog-Fake: Bestätigung und Texteingabe mit fester Antwort; merkt sich den zuletzt gezeigten Text.</summary>
internal sealed class FakePersonDialogs(bool confirm = true, string? prompt = null) : IDialogService
{
    public string? LastTitle { get; private set; }

    public string? LastMessage { get; private set; }

    public int Confirms { get; private set; }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
    {
        Confirms++;
        LastTitle = title;
        LastMessage = message;
        return Task.FromResult(confirm);
    }

    public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
        => Task.FromResult(DialogChoice.None);

    public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK")
    {
        LastTitle = title;
        return Task.FromResult(prompt);
    }

    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

    public string? PickFolder(string title) => null;
}
