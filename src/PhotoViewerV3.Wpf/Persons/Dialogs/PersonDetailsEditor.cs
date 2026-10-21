using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Faces.Assignment;

namespace PhotoViewerV3.Persons.Dialogs;

/// <summary>
/// Anfrage an den Personen-Dialog (0.2.47, ADR-0045): Benennen einer Gruppe bzw. Bearbeiten einer Person.
/// </summary>
/// <param name="Initial">Vorbelegung (Bearbeiten) oder null (neu).</param>
/// <param name="Relations">Bereits verwendete Beziehungen (Vorschlagsliste beim Tippen).</param>
/// <param name="ExistingNames">Anzeigenamen ANDERER Personen — passt der eingegebene Anzeigename, zeigt der Dialog <paramref name="ExistingHint"/>.</param>
/// <param name="ExistingHint">Hinweis mit Platzhalter {0} für den Namen, z. B. „Es gibt schon „{0}“ — die Gesichter werden ihr hinzugefügt.“</param>
/// <param name="CurrentMeName">Anzeigename der Person, die bisher „Das bin ich“ ist (außer der bearbeiteten), sonst null.</param>
public sealed record PersonEditRequest(
    string Title,
    string Message,
    string ConfirmText,
    PersonDetailsInput? Initial,
    IReadOnlyList<string> Relations,
    IReadOnlyCollection<string> ExistingNames,
    string ExistingHint,
    string? CurrentMeName);

/// <summary>Tag bzw. Monat in der Auswahl des Geburtsdatums (0 = keine Angabe, „–“).</summary>
public sealed record DateChoice(int Value, string Label);

/// <summary>
/// Zustand und Prüfung des Personen-Dialogs — ohne Fenster testbar. Anzeigename = Rufname, sonst „Vorname Nachname“;
/// Beziehung als Freitext mit Vorschlägen (gleicher Name ohne Groß-/Kleinschreibung → Schreibweise der vorhandenen);
/// Geburtsdatum ungefähr erlaubt (nur Jahr, Jahr + Monat).
/// </summary>
public sealed partial class PersonDetailsEditor : ObservableObject
{
    private readonly PersonEditRequest _request;
    private readonly int _currentYear;

    public PersonDetailsEditor(PersonEditRequest request, int? currentYear = null)
    {
        _request = request;
        _currentYear = currentYear ?? DateTime.Today.Year;
        Relations = request.Relations.OrderBy(r => r, StringComparer.CurrentCultureIgnoreCase).ToList();
        var initial = request.Initial;
        if (initial is not null)
        {
            _firstName = initial.FirstName;
            _lastName = initial.LastName;
            _nickname = initial.Nickname ?? string.Empty;
            _relation = initial.Relation ?? string.Empty;
            _birthYear = initial.BirthDate?.Year.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            _birthMonth = initial.BirthDate?.Month ?? 0;
            _birthDay = initial.BirthDate?.Day ?? 0;
            _isMe = initial.IsMe;
        }
    }

    public IReadOnlyList<string> Relations { get; }

    public static IReadOnlyList<DateChoice> Months { get; } =
        new[] { new DateChoice(0, "–") }
            .Concat(Enumerable.Range(1, 12).Select(m => new DateChoice(m, CultureInfo.GetCultureInfo("de-DE").DateTimeFormat.MonthNames[m - 1])))
            .ToList();

    public static IReadOnlyList<DateChoice> Days { get; } =
        new[] { new DateChoice(0, "–") }
            .Concat(Enumerable.Range(1, 31).Select(d => new DateChoice(d, d.ToString(CultureInfo.InvariantCulture))))
            .ToList();

    public string Title => _request.Title;

    public string Message => _request.Message;

    public string ConfirmText => _request.ConfirmText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(DisplayNameText), nameof(ExistingText), nameof(Error), nameof(CanConfirm))]
    private string _firstName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(DisplayNameText), nameof(ExistingText), nameof(Error), nameof(CanConfirm))]
    private string _lastName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(DisplayNameText), nameof(ExistingText), nameof(Error), nameof(CanConfirm))]
    private string _nickname = string.Empty;

    [ObservableProperty] private string _relation = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(CanConfirm), nameof(AgeText))]
    private string _birthYear = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(CanConfirm), nameof(AgeText))]
    private int _birthMonth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(CanConfirm), nameof(AgeText))]
    private int _birthDay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeText))]
    private bool _isMe;

    public string DisplayName => PersonNames.DisplayName(FirstName, LastName, Nickname);

    public string DisplayNameText => DisplayName.Length == 0 ? "Anzeigename: –" : $"Anzeigename: {DisplayName}";

    /// <summary>Hinweis, wenn der Anzeigename schon einer anderen Person gehört.</summary>
    public string? ExistingText
        => _request.ExistingNames.FirstOrDefault(n => string.Equals(n, DisplayName, StringComparison.CurrentCultureIgnoreCase)) is { } existing
            ? string.Format(CultureInfo.CurrentCulture, _request.ExistingHint, existing)
            : null;

    /// <summary>Hinweis zur „Ich“-Markierung, wenn sie bisher eine andere Person trägt.</summary>
    public string? MeText => IsMe && _request.CurrentMeName is { } me ? $"Bisher ist „{me}“ als „Das bin ich“ markiert — die Markierung wechselt." : null;

    /// <summary>„Alter heute: 46 Jahre“ bei gültigem Datum.</summary>
    public string? AgeText
        => TryBirthDate(out var date, out _) && date is { } d && d.FormatAgeOn(DateOnly.FromDateTime(DateTime.Today)) is { } age
            ? $"Alter heute: {age}"
            : null;

    /// <summary>Erster Fehler oder null.</summary>
    public string? Error
    {
        get
        {
            if (DisplayName.Length == 0)
            {
                return "Bitte einen Vornamen, Nachnamen oder Rufnamen angeben.";
            }

            return TryBirthDate(out _, out var error) ? null : error;
        }
    }

    public bool CanConfirm => Error is null;

    /// <summary>Eingabe für den Dienst; null bei Fehlern. Beziehung in der Schreibweise einer vorhandenen, falls gleich.</summary>
    public PersonDetailsInput? BuildInput()
    {
        if (!CanConfirm || !TryBirthDate(out var date, out _))
        {
            return null;
        }

        var relation = PersonNames.CleanOrNull(Relation);
        if (relation is not null && Relations.FirstOrDefault(r => string.Equals(r, relation, StringComparison.CurrentCultureIgnoreCase)) is { } known)
        {
            relation = known;
        }

        return new PersonDetailsInput(FirstName, LastName, Nickname, relation, date, IsMe).Normalized();
    }

    private bool TryBirthDate(out PersonBirthDate? date, out string? error)
    {
        int? year = null;
        var text = PersonNames.Clean(BirthYear);
        if (text.Length > 0)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                date = null;
                error = "Das Geburtsjahr bitte als Zahl angeben (z. B. 1980).";
                return false;
            }

            year = parsed;
        }

        return PersonBirthDate.TryCreate(year, BirthMonth == 0 ? null : BirthMonth, BirthDay == 0 ? null : BirthDay, _currentYear, out date, out error);
    }
}
