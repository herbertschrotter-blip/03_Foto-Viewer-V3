using System;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Implementierung von <see cref="INavigationService"/>: Das MainWindow bindet sich per <see cref="Attach"/>
/// mit einem Delegaten auf NavigationView.Navigate(tag) an und meldet Klick-Navigationen über
/// <see cref="NotifyNavigated"/> zurück, damit CurrentTag und das Navigated-Ereignis konsistent bleiben.
/// Reiter-Tags der Einstellungen (ADR-0042) setzen <see cref="SettingsSectionState.SelectedSection"/> und öffnen die
/// Einstellungen — so funktionieren Aufrufe wie „Tags verwalten…“ (NavigateTo(Tags)) ohne eigene Seite weiter.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private readonly ILogger<NavigationService> _logger;
    private readonly SettingsSectionState _sections;
    private Func<string, bool>? _navigate;
    private string? _pendingTag;

    public NavigationService(ILogger<NavigationService> logger, SettingsSectionState sections)
    {
        _logger = logger;
        _sections = sections;
    }

    public string? CurrentTag { get; private set; }

    public event EventHandler<string>? Navigated;

    /// <summary>Vom MainWindow aufgerufen, sobald die NavigationView navigieren kann.</summary>
    public void Attach(Func<string, bool> navigate)
    {
        _navigate = navigate;
        if (_pendingTag is { } pending)
        {
            _pendingTag = null;
            NavigateTo(pending);
        }
    }

    public void NavigateTo(string tag)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => NavigateTo(tag));
            return;
        }

        // Reiter der Einstellungen: Reiter wählen, dann die Einstellungen öffnen (ADR-0042)
        if (NavigationTags.IsSettingsSection(tag))
        {
            _sections.SelectedSection = tag;
            tag = NavigationTags.Settings;
        }

        if (_navigate is null)
        {
            _pendingTag = tag;
            return;
        }

        if (!_navigate(tag))
        {
            _logger.LogDebug("Navigation zu {Tag} nicht ausgeführt (Seite bereits aktiv oder unbekanntes Tag)", tag);
        }
    }

    /// <summary>Vom MainWindow nach NavigationView.SelectionChanged aufgerufen (UI-Thread).</summary>
    public void NotifyNavigated(string tag)
    {
        CurrentTag = tag;
        Navigated?.Invoke(this, tag);
    }
}
