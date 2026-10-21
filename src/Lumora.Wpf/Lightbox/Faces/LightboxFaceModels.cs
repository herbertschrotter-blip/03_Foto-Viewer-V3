using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Lumora.Domain.Enums;
using Lumora.Services.Faces.Assignment;

namespace Lumora.Lightbox.Faces;

/// <summary>Art eines Gesichtsrahmens in der Lightbox (Farbe und Beschriftung).</summary>
public enum LightboxFaceKind
{
    /// <summary>Bestätigt — beschriftet mit dem Namen.</summary>
    Confirmed,

    /// <summary>Vorschlag — „Vorschlag: X?“.</summary>
    Suggested,

    /// <summary>Noch keiner Person zugeordnet — „Unbekannt“.</summary>
    Unknown,
}

/// <summary>
/// Ein Gesichtsrahmen über dem Bild (0.2.38): Box relativ 0..1 zum orientierten Bild (wie <c>Faces.BoxX..BoxH</c>,
/// ADR-0035), damit sie bei jedem Zoom/Schwenk über <see cref="Controls.IZoomPanOverlay"/> mitwandert.
/// </summary>
public sealed class LightboxFaceBox
{
    public LightboxFaceBox(FacePersonInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        FaceId = info.FaceId;
        Box = new Rect(info.BoxRelative.X, info.BoxRelative.Y, Math.Max(0, info.BoxRelative.Width), Math.Max(0, info.BoxRelative.Height));
        PersonId = info.PersonId;
        PersonName = info.PersonName;
        SuggestedPersonId = info.SuggestedPersonId;
        SuggestedPersonName = info.SuggestedPersonName;
        SuggestionScore = info.SuggestionScore;
        Kind = info.State == FaceState.Confirmed && info.PersonId is not null
            ? LightboxFaceKind.Confirmed
            : info.State == FaceState.Suggested && info.SuggestedPersonId is not null
                ? LightboxFaceKind.Suggested
                : LightboxFaceKind.Unknown;
    }

    public long FaceId { get; }

    /// <summary>Box relativ zum Bild (0..1).</summary>
    public Rect Box { get; }

    public LightboxFaceKind Kind { get; }

    public int? PersonId { get; }

    public string? PersonName { get; }

    public int? SuggestedPersonId { get; }

    public string? SuggestedPersonName { get; }

    public float? SuggestionScore { get; }

    /// <summary>Beschriftung: Name / „Vorschlag: X?“ / „Unbekannt“.</summary>
    public string Label => Kind switch
    {
        LightboxFaceKind.Confirmed => PersonName ?? "Unbekannt",
        LightboxFaceKind.Suggested => $"Vorschlag: {SuggestedPersonName}?",
        _ => "Unbekannt",
    };

    /// <summary>Für Tooltip und UI-Automation.</summary>
    public string AutomationName => $"Gesicht: {Label}";
}

/// <summary>
/// Eintrag des Klick-Menüs auf einem Gesichtsrahmen (0.2.38). <see cref="Execute"/> null mit <see cref="Children"/> =
/// Untermenü; <see cref="IsSeparator"/> = Trennlinie. Das Fenster baut daraus ein ContextMenu, Tests rufen
/// <see cref="Execute"/> direkt auf.
/// </summary>
public sealed record FaceMenuItem(string Header, Func<Task>? Execute = null, IReadOnlyList<FaceMenuItem>? Children = null, bool IsSeparator = false)
{
    public static FaceMenuItem Separator { get; } = new(string.Empty, IsSeparator: true);
}
