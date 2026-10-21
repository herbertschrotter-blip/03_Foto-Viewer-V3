using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Places.Map.Tiles;
using Lumora.Services.Database;

namespace Lumora.Places.Styles;

/// <summary>
/// Fest vorgeschlagener Kartendienst (ADR-0055, Herbert 2026-10-21): Beschreibung, Anforderungs-, Website- und
/// Konto-Adressen (nur in der offiziellen Doku geprüfte URLs), Anleitung in drei Schritten. Lumora öffnet die Seiten nur
/// auf Klick im Standardbrowser — es registriert nichts und sendet selbst keine Daten.
/// </summary>
public sealed record MapServiceInfo(
    MapTileVendor Vendor,
    string Id,
    string Name,
    string Description,
    string StylesText,
    string? FreeQuotaText,
    string RequestKeyUrl,
    string DefaultWebsite,
    string DefaultAccountUrl,
    IReadOnlyList<string> Steps)
{
    /// <summary>CARTO: Anforderung carto.com/basemaps/apikey/ (Schlüssel per E-Mail), Konto dashboard.basemaps.carto.com
    /// („Verbrauch je Schlüssel, stündlich aktualisiert“ laut carto.com/basemaps).</summary>
    public static MapServiceInfo Carto { get; } = new(
        MapTileVendor.Carto,
        "carto",
        "CARTO",
        "Moderne Grundkarten von CARTO — kostenloser Schlüssel, nur für dich, nicht-kommerziell bis 5 Mio. Kachelabrufe im Monat; Kacheln höchstens 30 Tage zwischengespeichert, Namensnennung auf der Karte.",
        "Stile: Automatisch, Voyager, Positron, Dark Matter",
        "Gratis bis 5 Mio./Monat",
        "https://carto.com/basemaps/apikey/",
        "https://carto.com/basemaps",
        "https://dashboard.basemaps.carto.com",
        [
            "1. „API-Schlüssel anfordern…“ öffnet die CARTO-Seite — dort mit deiner E-Mail-Adresse anfordern (kein Konto nötig).",
            "2. Den Schlüssel aus der E-Mail von CARTO kopieren (kommt nach etwa einer Minute).",
            "3. Hier einfügen und „Schlüssel prüfen“ — danach sind Voyager, Positron und Dark Matter freigeschaltet.",
        ]);

    /// <summary>MapTiler: Schlüsselverwaltung cloud.maptiler.com/account/keys/ (führt ohne Konto zur Anmeldung),
    /// Verbrauch cloud.maptiler.com/account/analytics (laut docs.maptiler.com).</summary>
    public static MapServiceInfo MapTiler { get; } = new(
        MapTileVendor.MapTiler,
        "maptiler",
        "MapTiler",
        "Satellitenbilder von MapTiler — kostenloses Konto mit eigenem Schlüssel, Free-Plan nur nicht-kommerziell; persönlicher Cache, keine Massenabrufe, Namensnennung auf der Karte.",
        "Stil: Satellit",
        null,
        "https://cloud.maptiler.com/account/keys/",
        "https://www.maptiler.com",
        "https://cloud.maptiler.com/account/analytics",
        [
            "1. „API-Schlüssel anfordern…“ öffnet MapTiler Cloud — kostenloses Konto anlegen bzw. anmelden.",
            "2. Unter „API keys“ den Schlüssel kopieren.",
            "3. Hier einfügen und „Schlüssel prüfen“ — danach ist „Satellit“ freigeschaltet.",
        ]);

    public static IReadOnlyList<MapServiceInfo> All { get; } = [Carto, MapTiler];

    public static MapServiceInfo For(MapTileVendor vendor) => All.First(s => s.Vendor == vendor);
}

/// <summary>
/// Angaben des Benutzers zu einem Kartendienst (ohne den Schlüssel — der liegt in <see cref="IMapStyleService"/>):
/// Website, E-Mail der Registrierung, Konto-Link, Notiz und für welchen Schlüssel die letzte Prüfung erfolgreich war
/// (nur ein kurzer Hash, nie der Schlüssel). Alles nur lokal in der Settings-Tabelle des Katalogs.
/// </summary>
public sealed record MapServiceAccount(MapTileVendor Vendor, string? Website, string? Email, string? AccountUrl, string? Note, string? VerifiedKeyHash)
{
    public static MapServiceAccount Default(MapTileVendor vendor)
    {
        var info = MapServiceInfo.For(vendor);
        return new MapServiceAccount(vendor, info.DefaultWebsite, null, info.DefaultAccountUrl, null, null);
    }

    /// <summary>Kurzer Hash eines Schlüssels (12 Hex-Zeichen von SHA-256) — erkennt „dieser Schlüssel wurde geprüft“.</summary>
    public static string? HashKey(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())))[..12];

    public bool IsVerifiedFor(string? key) => VerifiedKeyHash is not null && VerifiedKeyHash == HashKey(key);
}

/// <summary>Links der Kartendienste: nur absolute http/https-Adressen (kein file:, javascript:, Programmaufruf).</summary>
public static class MapServiceLinks
{
    public static bool TryParse(string? text, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            && !string.IsNullOrEmpty(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        return false;
    }
}

/// <summary>Öffnet einen geprüften Link im Standardbrowser (nur auf Klick).</summary>
public interface IMapLinkOpener
{
    bool Open(Uri uri);
}

public sealed class ShellMapLinkOpener : IMapLinkOpener
{
    public bool Open(Uri uri)
    {
        if (!MapServiceLinks.TryParse(uri.AbsoluteUri, out var safe))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(safe!.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Ablage der Kartendienst-Angaben (Settings-Tabelle, Schlüssel <c>ui.map.&lt;dienst&gt;.&lt;feld&gt;</c>).</summary>
public interface IMapServiceAccountStore
{
    Task<MapServiceAccount> LoadAsync(MapTileVendor vendor, CancellationToken ct = default);

    Task SaveAsync(MapServiceAccount account, CancellationToken ct = default);
}

public sealed class MapServiceAccountStore : IMapServiceAccountStore
{
    private readonly ILibraryContextFactory _factory;

    public MapServiceAccountStore(ILibraryContextFactory factory) => _factory = factory;

    public static string SettingKey(MapTileVendor vendor, string field) => $"ui.map.{MapServiceInfo.For(vendor).Id}.{field}";

    public async Task<MapServiceAccount> LoadAsync(MapTileVendor vendor, CancellationToken ct = default)
    {
        var prefix = $"ui.map.{MapServiceInfo.For(vendor).Id}.";
        await using var ctx = _factory.Create();
        var rows = await ctx.Settings.AsNoTracking().Where(s => s.Key.StartsWith(prefix)).ToListAsync(ct).ConfigureAwait(false);
        string? Value(string field) => rows.FirstOrDefault(r => r.Key == prefix + field)?.Value;
        var defaults = MapServiceAccount.Default(vendor);
        var website = Value("website");
        var accountUrl = Value("accountUrl");
        return new MapServiceAccount(
            vendor,
            website is null ? defaults.Website : Normalize(website), // nie gespeichert → Vorbelegung; leer gespeichert → leer
            Normalize(Value("email")),
            accountUrl is null ? defaults.AccountUrl : Normalize(accountUrl),
            Normalize(Value("note")),
            Normalize(Value("verified")));
    }

    public async Task SaveAsync(MapServiceAccount account, CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        foreach (var (field, value) in new[]
                 {
                     ("website", account.Website), ("email", account.Email), ("accountUrl", account.AccountUrl),
                     ("note", account.Note), ("verified", account.VerifiedKeyHash),
                 })
        {
            var key = SettingKey(account.Vendor, field);
            var setting = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
            var stored = Normalize(value) ?? string.Empty;
            if (setting is null)
            {
                ctx.Settings.Add(new Setting { Key = key, Value = stored });
            }
            else
            {
                setting.Value = stored;
            }
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
