using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lumora.Stories.Assistant;

/// <summary>Eintrag im Menü eines Punkts der Karte („Nur Tag (212)“, „Ohne Zeitraum“ …).</summary>
public sealed partial class AssistantOption : ObservableObject
{
    private readonly Func<Task> _apply;

    public AssistantOption(string text, Func<Task> apply, bool isChecked = false)
    {
        Text = text;
        _apply = apply;
        IsChecked = isChecked;
    }

    public string Text { get; }

    public bool IsChecked { get; }

    [RelayCommand]
    private Task Apply() => _apply();
}

/// <summary>Änderbarer Wert der Karte (Mockup: Token mit Stift) — ein Klick öffnet das Menü <see cref="Options"/>.</summary>
public sealed partial class AssistantToken : ObservableObject
{
    public AssistantToken(string text, string? detail = null, bool isWarning = false, bool isPerson = false)
    {
        Text = text;
        Detail = detail;
        IsWarning = isWarning;
        IsPerson = isPerson;
    }

    public string Text { get; }

    public string? Detail { get; }

    /// <summary>„nicht gefunden“ — sichtbar markiert statt still weggelassen.</summary>
    public bool IsWarning { get; }

    public bool IsPerson { get; }

    /// <summary>Gesicht der Person (Personen-Punkte).</summary>
    public long? FaceId { get; init; }

    [ObservableProperty] private ImageSource? _image;

    public ObservableCollection<AssistantOption> Options { get; } = new();

    public bool HasOptions => Options.Count > 0;

    public string AutomationText => Detail is null ? Text : Text + ", " + Detail;
}

/// <summary>Zeile der Karte: Bezeichnung links, Werte rechts.</summary>
public sealed class AssistantFact
{
    public AssistantFact(string label, params AssistantToken[] tokens)
    {
        Label = label;
        Tokens = new ObservableCollection<AssistantToken>(tokens);
    }

    public string Label { get; }

    public ObservableCollection<AssistantToken> Tokens { get; }
}

/// <summary>Bild im Vorschau-Streifen der Karte.</summary>
public sealed partial class AssistantPreviewImage : ObservableObject
{
    public AssistantPreviewImage(int fileId) => FileId = fileId;

    public int FileId { get; }

    [ObservableProperty] private ImageSource? _image;
}

/// <summary>Karte „So habe ich es verstanden — stimmt das?“.</summary>
public sealed class AssistantCard
{
    public AssistantCard(IEnumerable<AssistantFact> facts, string countText, string resultText, IEnumerable<AssistantPreviewImage> preview, IEnumerable<string> notes, bool canCreate, string createText)
    {
        Facts = new ObservableCollection<AssistantFact>(facts);
        CountText = countText;
        ResultText = resultText;
        Preview = new ObservableCollection<AssistantPreviewImage>(preview);
        Notes = new ObservableCollection<string>(notes);
        CanCreate = canCreate;
        CreateText = createText;
    }

    public ObservableCollection<AssistantFact> Facts { get; }

    /// <summary>Große Zahl („312“).</summary>
    public string CountText { get; }

    public string ResultText { get; }

    public ObservableCollection<AssistantPreviewImage> Preview { get; }

    public ObservableCollection<string> Notes { get; }

    public bool HasNotes => Notes.Count > 0;

    public bool CanCreate { get; }

    public string CreateText { get; }
}

/// <summary>Auswahl einer Rückfrage (mit Gesicht und Fotozahl).</summary>
public sealed partial class AssistantChoice : ObservableObject
{
    private readonly Func<Task> _choose;

    public AssistantChoice(string label, string? detail, Func<Task> choose, long? faceId = null, int? faceLibraryId = null)
    {
        Label = label;
        Detail = detail;
        _choose = choose;
        FaceId = faceId;
        FaceLibraryId = faceLibraryId;
    }

    public string Label { get; }

    public string? Detail { get; }

    public long? FaceId { get; }

    public int? FaceLibraryId { get; }

    public bool HasFace => FaceId is not null;

    [ObservableProperty] private ImageSource? _image;

    [RelayCommand]
    private Task Choose() => _choose();
}

/// <summary>Rückfrage-Karte („Wer ist „Oma“?“).</summary>
public sealed class AssistantQuestionCard
{
    public AssistantQuestionCard(string title, string text, string? note, IEnumerable<AssistantChoice> choices)
    {
        Title = title;
        Text = text;
        Note = note;
        Choices = new ObservableCollection<AssistantChoice>(choices);
    }

    public string Title { get; }

    public string Text { get; }

    public string? Note { get; }

    public ObservableCollection<AssistantChoice> Choices { get; }
}

/// <summary>Zeile „Zuletzt gefragt“.</summary>
public sealed class AssistantHistoryRow
{
    public AssistantHistoryRow(int id, string sentence, string intentJson, bool isSlideshow, StoryAssistantViewModel owner)
    {
        Id = id;
        Sentence = sentence;
        IntentJson = intentJson;
        IsSlideshow = isSlideshow;
        Owner = owner;
    }

    public int Id { get; }

    public string Sentence { get; }

    public string IntentJson { get; }

    public bool IsSlideshow { get; }

    public string ActionText => IsSlideshow ? "Wieder abspielen" : "Öffnen";

    public StoryAssistantViewModel Owner { get; }
}
