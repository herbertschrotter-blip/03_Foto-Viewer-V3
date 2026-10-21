using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumora.Places.Map.Tiles;

namespace Lumora.Places.Styles;

/// <summary>Einrichtungsstand eines Kartendienstes.</summary>
public enum MapServiceStatus
{
    /// <summary>Kein Schlüssel — noch nicht eingerichtet.</summary>
    NotSetUp,

    /// <summary>Schlüssel eingetragen, aber (für diesen Schlüssel) noch nicht erfolgreich geprüft.</summary>
    NotVerified,

    /// <summary>Schlüssel geprüft: eingerichtet.</summary>
    Ready,

    /// <summary>Die letzte Prüfung wurde vom Anbieter abgelehnt.</summary>
    Rejected,
}

/// <summary>
/// Karte „Kartendienst“ (ADR-0055, Herbert 2026-10-21) je Dienst (CARTO, MapTiler) in Einstellungen › Darstellung › Karte:
/// vorgeschlagen auch ohne Schlüssel (Beschreibung, Status, „API-Schlüssel anfordern…“, Anleitung in drei Schritten),
/// Schlüssel (Passwortfeld) mit „Schlüssel prüfen“, Website, E-Mail der Registrierung, Konto-Link, Notiz, Lumoras eigene
/// Zählung des Monats und „Verbrauch im Konto ansehen“. Links öffnen nur auf Klick im Standardbrowser, nur http/https.
/// </summary>
public sealed partial class MapServiceAccountViewModel : ObservableObject
{
    private readonly IMapKeyValidator _validator;
    private readonly IMapLinkOpener? _links;
    private readonly Func<MapServiceAccountViewModel, Task> _saveVerified;
    private MapServiceAccount _saved;
    private string? _savedKey;
    private bool _lastCheckRejected;

    public MapServiceAccountViewModel(MapServiceInfo info, IMapKeyValidator validator, IMapLinkOpener? links, Func<MapServiceAccountViewModel, Task> saveVerified)
    {
        Info = info;
        _validator = validator;
        _links = links;
        _saveVerified = saveVerified;
        _saved = MapServiceAccount.Default(info.Vendor);
        Apply(_saved, null);
    }

    public MapServiceInfo Info { get; }

    public MapTileVendor Vendor => Info.Vendor;

    public string Title => Info.Name;

    public IReadOnlyList<string> Steps => Info.Steps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(StatusText), nameof(IsReady), nameof(IsNotReady))]
    [NotifyCanExecuteChangedFor(nameof(CheckKeyCommand))]
    private string? _key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebsiteError))]
    [NotifyCanExecuteChangedFor(nameof(OpenWebsiteCommand))]
    private string? _website;

    [ObservableProperty] private string? _email;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountUrlError))]
    [NotifyCanExecuteChangedFor(nameof(OpenAccountCommand))]
    private string? _accountUrl;

    [ObservableProperty] private string? _note;

    [ObservableProperty] private string? _checkText;

    [ObservableProperty] private bool? _checkOk;

    [ObservableProperty] private string _usageText = string.Empty;

    /// <summary>Hash des zuletzt erfolgreich geprüften Schlüssels (nie der Schlüssel).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(StatusText), nameof(IsReady), nameof(IsNotReady))]
    private string? _verifiedKeyHash;

    public MapServiceStatus Status
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Key))
            {
                return MapServiceStatus.NotSetUp;
            }

            if (VerifiedKeyHash is not null && VerifiedKeyHash == MapServiceAccount.HashKey(Key))
            {
                return MapServiceStatus.Ready;
            }

            return _lastCheckRejected ? MapServiceStatus.Rejected : MapServiceStatus.NotVerified;
        }
    }

    public string StatusText => Status switch
    {
        MapServiceStatus.NotSetUp => "Kein Schlüssel – noch nicht eingerichtet",
        MapServiceStatus.Ready => "Eingerichtet",
        MapServiceStatus.Rejected => "Schlüssel abgelehnt – bitte prüfen",
        _ => "Schlüssel eingetragen – noch nicht geprüft",
    };

    public bool IsReady => Status == MapServiceStatus.Ready;

    public bool IsNotReady => !IsReady;

    /// <summary>Fehlertext, wenn die Website keine http/https-Adresse ist (leer erlaubt).</summary>
    public string? WebsiteError => LinkError(Website);

    public string? AccountUrlError => LinkError(AccountUrl);

    public bool HasValidLinks => WebsiteError is null && AccountUrlError is null;

    /// <summary>Weicht etwas von den gespeicherten Werten ab?</summary>
    public bool HasChanges
        => N(Key) != _savedKey || N(Website) != _saved.Website || N(Email) != _saved.Email
           || N(AccountUrl) != _saved.AccountUrl || N(Note) != _saved.Note || VerifiedKeyHash != _saved.VerifiedKeyHash;

    /// <summary>Gespeicherte Werte übernehmen (Laden, nach dem Speichern).</summary>
    public void Apply(MapServiceAccount account, string? key)
    {
        _saved = account;
        _savedKey = N(key);
        Key = key;
        Website = account.Website;
        Email = account.Email;
        AccountUrl = account.AccountUrl;
        Note = account.Note;
        VerifiedKeyHash = account.VerifiedKeyHash;
    }

    /// <summary>Werte zum Speichern (Schlüssel getrennt in <see cref="IMapStyleService"/>).</summary>
    public MapServiceAccount ToAccount() => new(Vendor, N(Website), N(Email), N(AccountUrl), N(Note), VerifiedKeyHash);

    /// <summary>Lumoras Zählung des laufenden Monats anzeigen.</summary>
    public void ShowUsage(MapTileUsage usage)
    {
        var count = usage.Count.ToString("N0", CultureInfo.GetCultureInfo("de-AT"));
        var stand = usage.UpdatedUtc is { } at ? $" (Stand {at.ToLocalTime():dd.MM.yyyy HH:mm})" : string.Empty;
        UsageText = $"Diesen Monat von Lumora abgerufen: {count} Kacheln{stand}"
                    + (Info.FreeQuotaText is { } quota ? $" · {quota}" : string.Empty);
    }

    [RelayCommand]
    private void RequestKey() => OpenLink(Info.RequestKeyUrl);

    [RelayCommand(CanExecute = nameof(CanOpenWebsite))]
    private void OpenWebsite() => OpenLink(Website);

    private bool CanOpenWebsite() => MapServiceLinks.TryParse(Website, out _);

    /// <summary>„Konto öffnen“ und „Verbrauch im Konto ansehen“.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenAccount))]
    private void OpenAccount() => OpenLink(AccountUrl);

    private bool CanOpenAccount() => MapServiceLinks.TryParse(AccountUrl, out _);

    [RelayCommand(CanExecute = nameof(CanCheckKey))]
    private async Task CheckKeyAsync()
    {
        CheckText = "Prüfe …";
        CheckOk = null;
        var result = await _validator.CheckAsync(Vendor, Key).ConfigureAwait(true);
        CheckText = result.Message;
        CheckOk = result.IsValid;
        _lastCheckRejected = result.IsValid == false;
        if (result.IsValid == true)
        {
            VerifiedKeyHash = MapServiceAccount.HashKey(Key);
            await _saveVerified(this).ConfigureAwait(true); // geprüft → gleich speichern, Stile sind freigeschaltet
        }

        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsNotReady));
    }

    private bool CanCheckKey() => !string.IsNullOrWhiteSpace(Key);

    partial void OnKeyChanged(string? value) => _lastCheckRejected = false;

    private void OpenLink(string? text)
    {
        if (MapServiceLinks.TryParse(text, out var uri) && _links is not null)
        {
            _links.Open(uri!);
        }
    }

    private static string? LinkError(string? text)
        => string.IsNullOrWhiteSpace(text) || MapServiceLinks.TryParse(text, out _) ? null : "Nur Adressen mit http:// oder https://";

    private static string? N(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
