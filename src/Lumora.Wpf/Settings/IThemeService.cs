using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Settings;

/// <summary>Farbschema der App; wird in der Settings-Tabelle (Key „ui.theme“) gespeichert.</summary>
public enum AppThemeMode
{
    /// <summary>Folgt dem Windows-Farbschema (Standard).</summary>
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>Theme-Umschaltung (WPF-UI ApplicationThemeManager) inkl. Persistenz in der Settings-Tabelle.</summary>
public interface IThemeService
{
    AppThemeMode Mode { get; }

    event EventHandler<AppThemeMode>? ModeChanged;

    /// <summary>Beim App-Start: gespeicherten Modus lesen und anwenden (Default System).</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Modus anwenden und speichern.</summary>
    Task SetModeAsync(AppThemeMode mode, CancellationToken ct = default);
}
