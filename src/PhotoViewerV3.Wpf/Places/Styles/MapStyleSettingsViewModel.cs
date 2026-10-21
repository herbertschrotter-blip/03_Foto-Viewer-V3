using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Places.Map.Tiles;

namespace PhotoViewerV3.Places.Styles;

/// <summary>
/// Karte „Karte“ in Einstellungen › Darstellung (ADR-0055): Kartenstil wählen (wie das Stil-Menü auf der Karte) und je
/// Kartendienst (CARTO, MapTiler) eine vorgeschlagene Karte „Kartendienst“ (<see cref="MapServiceAccountViewModel"/>:
/// Schlüssel, Prüfen, Website, E-Mail, Konto, Notiz, Lumoras Zählung). „Kartendienste speichern“ speichert Schlüssel und
/// Angaben; eine erfolgreiche Prüfung speichert sofort.
/// </summary>
public sealed partial class MapStyleSettingsViewModel : ObservableObject
{
    private readonly IMapStyleService _styles;
    private readonly IMapServiceAccountStore? _accounts;
    private readonly IMapTileUsageCounter? _usage;
    private readonly ILogger _logger;
    private bool _loading;

    public MapStyleSettingsViewModel(
        IMapStyleService styles,
        IMapKeyValidator validator,
        IMapServiceAccountStore? accounts = null,
        IMapTileUsageCounter? usage = null,
        IMapLinkOpener? links = null,
        ILogger<MapStyleSettingsViewModel>? logger = null)
    {
        _styles = styles;
        _accounts = accounts;
        _usage = usage;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Carto = new MapServiceAccountViewModel(MapServiceInfo.Carto, validator, links, SaveAfterCheckAsync);
        MapTiler = new MapServiceAccountViewModel(MapServiceInfo.MapTiler, validator, links, SaveAfterCheckAsync);
        Carto.PropertyChanged += OnServicePropertyChanged;
        MapTiler.PropertyChanged += OnServicePropertyChanged;
        _styles.Changed += (_, _) => PhotoViewerV3.Shell.UiThread.Post(ApplyStyleFromService);
        ApplyStyleFromService();
        ApplyKeysIfUnchanged();
    }

    public IReadOnlyList<MapStyleOption> StyleOptions => MapStyleCatalog.Options;

    /// <summary>Kartendienst CARTO (Voyager, Positron, Dark Matter, Automatisch).</summary>
    public MapServiceAccountViewModel Carto { get; }

    /// <summary>Kartendienst MapTiler (Satellit).</summary>
    public MapServiceAccountViewModel MapTiler { get; }

    public IReadOnlyList<MapServiceAccountViewModel> Services => [Carto, MapTiler];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StyleHint), nameof(HasStyleHint))]
    private MapStyleOption? _selectedStyle;

    /// <summary>Eingaben weichen von den gespeicherten ab und alle Links sind gültig („Kartendienste speichern“ aktiv).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _hasUnsavedChanges;

    [ObservableProperty] private string? _savedText;

    /// <summary>Hinweis unter der Stilauswahl, wenn der gewählte Stil einen Schlüssel braucht, der fehlt.</summary>
    public string? StyleHint
    {
        get
        {
            var keys = _styles.Keys;
            return SelectedStyle?.Style switch
            {
                MapStyle.Automatic when !keys.HasCarto => "Ohne CARTO-Schlüssel zeigt „Automatisch“ die OpenStreetMap-Standardkarte.",
                MapStyle.Voyager or MapStyle.Positron or MapStyle.DarkMatter when !keys.HasCarto => MapStyleCatalog.KeyMissingNotice + " (CARTO, unten). Bis dahin OpenStreetMap.",
                MapStyle.Satellite when !keys.HasMapTiler => MapStyleCatalog.KeyMissingNotice + " (MapTiler, unten). Bis dahin OpenStreetMap.",
                _ => null,
            };
        }
    }

    public bool HasStyleHint => StyleHint is not null;

    partial void OnSelectedStyleChanged(MapStyleOption? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _ = SaveStyleAsync(value.Style);
    }

    /// <summary>Beim Anzeigen: gespeicherte Werte und Zählung lesen.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        await _styles.EnsureLoadedAsync().ConfigureAwait(true);
        ApplyStyleFromService();
        foreach (var service in Services)
        {
            var account = _accounts is null ? MapServiceAccount.Default(service.Vendor) : await LoadAccountAsync(service.Vendor).ConfigureAwait(true);
            if (!service.HasChanges || _accounts is not null)
            {
                service.Apply(account, _styles.Keys.For(service.Vendor));
            }
        }

        await RefreshUsageAsync().ConfigureAwait(true);
        UpdateUnsaved();
    }

    /// <summary>Lumoras Zählung je Dienst neu anzeigen.</summary>
    public async Task RefreshUsageAsync()
    {
        if (_usage is null)
        {
            return;
        }

        try
        {
            await _usage.EnsureLoadedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Kachel-Zählung nicht gelesen");
        }

        foreach (var service in Services)
        {
            service.ShowUsage(_usage.Get(service.Vendor));
        }
    }

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private async Task SaveAsync()
    {
        if (Services.Any(s => !s.HasValidLinks))
        {
            SavedText = "Links bitte als http:// oder https:// eintragen.";
            return;
        }

        try
        {
            await _styles.SetKeysAsync(new MapApiKeys(Carto.Key, MapTiler.Key)).ConfigureAwait(true);
            foreach (var service in Services)
            {
                var account = service.ToAccount();
                if (_accounts is not null)
                {
                    await _accounts.SaveAsync(account).ConfigureAwait(true);
                }

                service.Apply(account, _styles.Keys.For(service.Vendor));
            }

            SavedText = "Kartendienste gespeichert.";
            UpdateUnsaved();
            OnPropertyChanged(nameof(StyleHint));
            OnPropertyChanged(nameof(HasStyleHint));
        }
        catch (Exception ex)
        {
            // Nur die Art des Fehlers — Schlüssel und E-Mail nie ins Protokoll
            _logger.LogWarning("Kartendienste konnten nicht gespeichert werden: {Error}", ex.GetType().Name);
            SavedText = "Speichern fehlgeschlagen: " + ex.Message;
        }
    }

    /// <summary>Erfolgreich geprüft: Schlüssel und Angaben sofort speichern (Dienst eingerichtet, Stile freigeschaltet).</summary>
    private async Task SaveAfterCheckAsync(MapServiceAccountViewModel service)
    {
        HasUnsavedChanges = true;
        await SaveAsync().ConfigureAwait(true);
        await RefreshUsageAsync().ConfigureAwait(true);
    }

    private async Task<MapServiceAccount> LoadAccountAsync(MapTileVendor vendor)
    {
        try
        {
            return await _accounts!.LoadAsync(vendor).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Kartendienst-Angaben nicht lesbar: {Error}", ex.GetType().Name);
            return MapServiceAccount.Default(vendor);
        }
    }

    private async Task SaveStyleAsync(MapStyle style)
    {
        try
        {
            await _styles.SetStyleAsync(style).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kartenstil konnte nicht gespeichert werden");
        }
    }

    private void ApplyStyleFromService()
    {
        _loading = true;
        try
        {
            SelectedStyle = StyleOptions.First(o => o.Style == _styles.Style);
        }
        finally
        {
            _loading = false;
        }

        ApplyKeysIfUnchanged();
        OnPropertyChanged(nameof(StyleHint));
        OnPropertyChanged(nameof(HasStyleHint));
    }

    /// <summary>Schlüssel aus dem Dienst übernehmen, solange der Benutzer nichts Eigenes eingetippt hat.</summary>
    private void ApplyKeysIfUnchanged()
    {
        foreach (var service in Services)
        {
            if (!service.HasChanges)
            {
                service.Apply(service.ToAccount(), _styles.Keys.For(service.Vendor));
            }
        }
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapServiceAccountViewModel.Key) or nameof(MapServiceAccountViewModel.Website)
            or nameof(MapServiceAccountViewModel.Email) or nameof(MapServiceAccountViewModel.AccountUrl)
            or nameof(MapServiceAccountViewModel.Note) or nameof(MapServiceAccountViewModel.VerifiedKeyHash))
        {
            UpdateUnsaved();
        }
    }

    private void UpdateUnsaved()
    {
        HasUnsavedChanges = Services.Any(s => s.HasChanges) && Services.All(s => s.HasValidLinks);
        if (Services.Any(s => s.HasChanges))
        {
            SavedText = null;
        }
    }
}
