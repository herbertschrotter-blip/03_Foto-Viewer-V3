using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Domain.Enums;
using Lumora.Services.Stories;
using Wpf.Ui.Controls;

namespace Lumora.Stories;

/// <summary>Rundes Personen-Bild auf Karte und Titelbild (Gesichts-Ausschnitt, ADR-0037).</summary>
public sealed partial class StoryAvatarViewModel : ObservableObject
{
    public StoryAvatarViewModel(StoryAvatarInfo info) => Info = info;

    public StoryAvatarInfo Info { get; }

    public string Name => Info.Name;

    /// <summary>Erster Buchstabe, solange das Gesicht noch lädt bzw. fehlt.</summary>
    public string Initial => string.IsNullOrEmpty(Info.Name) ? "?" : Info.Name[..1].ToUpperInvariant();

    [ObservableProperty] private ImageSource? _image;
}

/// <summary>
/// Karte einer Story in der Übersicht (Mockup 09): Titelbild-Collage aus drei Vorschaubildern, Art-Abzeichen, Anheft-Symbol,
/// sachliche Überschrift, Orte bzw. Untertitel, Zähler und Personen. Bilder und Personen kommen nach, sobald die Reihe
/// sichtbar wird.
/// </summary>
public sealed partial class StoryCardViewModel : ObservableObject
{
    public StoryCardViewModel(StoryCard card) => Card = card;

    public StoryCard Card { get; }

    /// <summary>Seite (Befehle der Karte).</summary>
    public StoriesViewModel? Owner { get; set; }

    /// <summary>Breite der Karte in der Reihe (DIP).</summary>
    [ObservableProperty] private double _width = StoriesViewModel.MinCardWidth;

    public int Id => Card.Id;

    public string Title => Card.Title;

    public string? Subtitle => Card.Subtitle;

    public string CountsText => Card.CountsText;

    public string KindLabel => Card.KindLabel;

    public bool IsPinned => Card.IsPinned;

    public bool IsHidden => Card.IsHidden;

    public bool IsPerson => Card.Kind == StoryKind.Person;

    public SymbolRegular KindSymbol => StoryKindVisuals.Symbol(Card.Kind, Card.IsAway);

    /// <summary>Automation: „Reise: 12.–19. Juli 2025, Grado · Triest — Italien, 421 Fotos · 9 Videos“.</summary>
    public string AutomationText => $"{Card.KindLabel}: {Card.Title}{(Card.Subtitle is { Length: > 0 } s ? ", " + s : string.Empty)}, {Card.CountsText}";

    /// <summary>Vorschaubild-Ids (Titelbild zuerst); null, solange noch nicht geladen.</summary>
    public IReadOnlyList<int>? PreviewIds { get; set; }

    public bool PersonsLoaded { get; set; }

    [ObservableProperty] private ImageSource? _image1;
    [ObservableProperty] private ImageSource? _image2;
    [ObservableProperty] private ImageSource? _image3;

    /// <summary>Gesicht der Person (Personen-Story).</summary>
    [ObservableProperty] private ImageSource? _personFace;

    public ObservableCollection<StoryAvatarViewModel> Avatars { get; } = new();

    /// <summary>Setzt die Bilder der Collage aus dem Cache (Titelbild zuerst).</summary>
    public void ApplyImages(Func<int, ImageSource?> lookup)
    {
        if (PreviewIds is not { } ids)
        {
            return;
        }

        Image1 = ids.Count > 0 ? lookup(ids[0]) ?? Image1 : null;
        Image2 = ids.Count > 1 ? lookup(ids[1]) ?? Image2 : null;
        Image3 = ids.Count > 2 ? lookup(ids[2]) ?? Image3 : null;
    }

    public bool UsesFile(int fileId) => PreviewIds?.Contains(fileId) == true;
}

/// <summary>Rückblick von heute (Banner bzw. Karte im Filter „Rückblicke“): dynamisch, wird beim Öffnen gespeichert.</summary>
public sealed partial class StoryMemoryViewModel : ObservableObject
{
    public StoryMemoryViewModel(StoryMemory memory) => Memory = memory;

    public StoryMemory Memory { get; }

    public StoriesViewModel? Owner { get; set; }

    public string Kicker => Memory.Kicker;

    public string Title => Memory.Title;

    /// <summary>„Wien · 48 Fotos · 2 Videos“.</summary>
    public string MetaText => Memory.Subtitle is { Length: > 0 } places ? places + StoryTitleBuilder.Dot + Memory.CountsText : Memory.CountsText;

    public string AutomationText => $"{Memory.Kicker}: {Memory.Title}, {MetaText}";

    /// <summary>Bis zu fünf Bilder für das Banner (Titelbild zuerst).</summary>
    public ObservableCollection<StoryTileImage> Images { get; } = new();

    // Feste Plätze des Banners (groß links, vier kleine) — ohne Index-Bindungen, die bei weniger Bildern Fehler meldeten.
    [ObservableProperty] private ImageSource? _image1;
    [ObservableProperty] private ImageSource? _image2;
    [ObservableProperty] private ImageSource? _image3;
    [ObservableProperty] private ImageSource? _image4;
    [ObservableProperty] private ImageSource? _image5;

    public void ApplyImages(Func<int, ImageSource?> lookup)
    {
        foreach (var image in Images)
        {
            image.Image = lookup(image.FileId) ?? image.Image;
        }

        Image1 = Images.Count > 0 ? Images[0].Image : null;
        Image2 = Images.Count > 1 ? Images[1].Image : null;
        Image3 = Images.Count > 2 ? Images[2].Image : null;
        Image4 = Images.Count > 3 ? Images[3].Image : null;
        Image5 = Images.Count > 4 ? Images[4].Image : null;
    }
}

/// <summary>Ein Bild im Rückblick-Banner.</summary>
public sealed partial class StoryTileImage : ObservableObject
{
    public StoryTileImage(int fileId) => FileId = fileId;

    public int FileId { get; }

    [ObservableProperty] private ImageSource? _image;
}

/// <summary>Symbole der Story-Arten (wie im Mockup: Reise ✈, Ereignis/Ausflug, Rückblick, Person, Ort, eigene).</summary>
public static class StoryKindVisuals
{
    public static SymbolRegular Symbol(StoryKind kind, bool isAway = false) => kind switch
    {
        StoryKind.Trip => SymbolRegular.Airplane24,
        StoryKind.Event => isAway ? SymbolRegular.VehicleCar24 : SymbolRegular.CalendarStar24,
        StoryKind.Memory => SymbolRegular.History24,
        StoryKind.Person => SymbolRegular.Person24,
        StoryKind.Place => SymbolRegular.Location24,
        _ => SymbolRegular.BookOpen24,
    };

    /// <summary>Brotkrumen-Abschnitt: „Reisen &amp; Ereignisse“, „Rückblicke“, „Personen“, „Orte“, „Eigene Storys“.</summary>
    public static string Section(StoryKind kind) => kind switch
    {
        StoryKind.Trip or StoryKind.Event => "Reisen & Ereignisse",
        StoryKind.Memory => "Rückblicke",
        StoryKind.Person => "Personen",
        StoryKind.Place => "Orte",
        _ => "Eigene Storys",
    };
}
