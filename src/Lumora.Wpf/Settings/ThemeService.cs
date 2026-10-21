using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Services.Database;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Lumora.Settings;

/// <summary>
/// Liest/schreibt den Theme-Modus in der Settings-Tabelle und wendet ihn über
/// <see cref="ApplicationThemeManager"/> an. „System“ übernimmt das aktuelle Windows-Farbschema;
/// das Live-Mitlaufen bei Wechseln erledigt das MainWindow per SystemThemeWatcher.
/// </summary>
public sealed class ThemeService : IThemeService
{
    public const string SettingKey = "ui.theme";

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<ThemeService> _logger;

    public ThemeService(ILibraryContextFactory factory, ILogger<ThemeService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public AppThemeMode Mode { get; private set; } = AppThemeMode.System;

    public event EventHandler<AppThemeMode>? ModeChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var mode = AppThemeMode.System;
        try
        {
            await using var ctx = _factory.Create();
            var setting = await ctx.Settings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == SettingKey, ct)
                .ConfigureAwait(true);
            if (setting?.Value is { } value && Enum.TryParse<AppThemeMode>(value, ignoreCase: true, out var parsed))
            {
                mode = parsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Theme-Einstellung konnte nicht gelesen werden — Standard: System");
        }

        Mode = mode;
        ApplyOnUiThread(mode);
        _logger.LogInformation("Theme angewendet: {Mode} (App-Theme {AppTheme})", mode, ApplicationThemeManager.GetAppTheme());
    }

    public async Task SetModeAsync(AppThemeMode mode, CancellationToken ct = default)
    {
        Mode = mode;
        ApplyOnUiThread(mode);

        await using (var ctx = _factory.Create())
        {
            var setting = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct).ConfigureAwait(true);
            if (setting is null)
            {
                ctx.Settings.Add(new Setting { Key = SettingKey, Value = mode.ToString() });
            }
            else
            {
                setting.Value = mode.ToString();
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(true);
        }

        _logger.LogInformation("Theme gespeichert: {Mode}", mode);
        ModeChanged?.Invoke(this, mode);
    }

    private static void ApplyOnUiThread(AppThemeMode mode)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply(mode);
        }
        else
        {
            dispatcher.Invoke(() => Apply(mode));
        }
    }

    private static void Apply(AppThemeMode mode)
    {
        switch (mode)
        {
            case AppThemeMode.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true);
                break;
            case AppThemeMode.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: true);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
                break;
        }
    }
}
