using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Abstractions;

namespace Lumora.Shell;

/// <summary>
/// Liefert der WPF-UI NavigationView die Seiteninstanzen aus dem DI-Container und cached sie je Typ —
/// jede Seite wird genau einmal erzeugt, unabhängig von ihrer DI-Lebensdauer (z. B. GalleryView = Transient).
/// </summary>
public sealed class NavigationPageProvider : INavigationViewPageProvider
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NavigationPageProvider> _logger;
    private readonly Dictionary<Type, object> _pages = new();

    public NavigationPageProvider(IServiceProvider services, ILogger<NavigationPageProvider> logger)
    {
        _services = services;
        _logger = logger;
    }

    public object? GetPage(Type pageType)
    {
        if (_pages.TryGetValue(pageType, out var page))
        {
            return page;
        }

        page = _services.GetRequiredService(pageType);
        _pages[pageType] = page;
        _logger.LogInformation("Seite erzeugt und gecacht: {Page}", pageType.Name);
        return page;
    }
}
