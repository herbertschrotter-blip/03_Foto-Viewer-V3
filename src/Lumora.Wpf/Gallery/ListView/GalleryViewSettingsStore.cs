using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Services.Database;

namespace Lumora.Gallery.ListView;

/// <summary>Ansichts-Einstellungen der Galerie (Grid/Liste, Spalten, Vorschaugröße) in der Settings-Tabelle.</summary>
public interface IGalleryViewSettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string? value, CancellationToken ct = default);
}

/// <summary>Key-Value-Zugriff auf die Settings-Tabelle (wie <c>ThemeService</c>); Lesefehler ergeben null.</summary>
public sealed class GalleryViewSettingsStore : IGalleryViewSettingsStore
{
    public const string ViewModeKey = "ui.gallery.viewMode";
    public const string ListColumnsKey = "ui.gallery.listColumns";
    public const string TileSizeKey = "ui.gallery.tileSize";

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger<GalleryViewSettingsStore> _logger;

    public GalleryViewSettingsStore(ILibraryContextFactory factory, ILogger<GalleryViewSettingsStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await using var ctx = _factory.Create();
            var setting = await ctx.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
            return setting?.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einstellung {Key} konnte nicht gelesen werden", key);
            return null;
        }
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var setting = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
        if (setting is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
