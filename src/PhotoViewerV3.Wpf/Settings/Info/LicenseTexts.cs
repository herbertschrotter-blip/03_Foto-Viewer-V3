namespace PhotoViewerV3.Settings.Info;

/// <summary>
/// Lizenzen der verwendeten Bausteine für „Alle Lizenzen anzeigen…“ in Einstellungen › Info (ADR-0042). Fester Text in der
/// App, kein Netzzugriff. Bei einem neuen Paket in Directory.Packages.props hier ergänzen.
/// </summary>
public static class LicenseTexts
{
    public const string Title = "Lizenzen der verwendeten Bausteine";

    public const string Intro =
        "Lumora nutzt die folgenden Bausteine. Die vollständigen Lizenztexte liegen den jeweiligen Paketen bei " +
        "(Quelle: nuget.org bzw. Projektseite).";

    public const string Details =
        "WPF-UI (lepoco/wpfui) — MIT-Lizenz\n" +
        "  Copyright (c) Leszek Pomianowski und WPF-UI-Mitwirkende\n\n" +
        "CommunityToolkit.Mvvm (.NET Community Toolkit) — MIT-Lizenz\n" +
        "  Copyright (c) .NET Foundation und Mitwirkende\n\n" +
        ".NET, WPF, Microsoft.Extensions.* (Hosting, DependencyInjection, Logging) — MIT-Lizenz\n" +
        "  Copyright (c) .NET Foundation und Mitwirkende\n\n" +
        "Entity Framework Core, Microsoft.Data.Sqlite — MIT-Lizenz\n" +
        "  Copyright (c) .NET Foundation und Mitwirkende\n\n" +
        "SQLite — gemeinfrei (Public Domain)\n" +
        "SQLitePCLRaw — Apache-Lizenz 2.0, Copyright (c) Eric Sink\n\n" +
        "SharpCompress — MIT-Lizenz\n" +
        "  Copyright (c) Adam Hathcock\n\n" +
        "ONNX Runtime mit DirectML (Microsoft.ML.OnnxRuntime.DirectML) — MIT-Lizenz\n" +
        "  Copyright (c) Microsoft Corporation\n\n" +
        "LibVLCSharp — LGPL 2.1; LibVLC (VideoLAN) — LGPL 2.1 oder neuer\n" +
        "  Copyright (c) VideoLAN und Mitwirkende\n\n" +
        "Serilog (Extensions.Hosting, Sinks.File, Sinks.Console) — Apache-Lizenz 2.0\n" +
        "  Copyright (c) Serilog-Mitwirkende\n\n" +
        "InsightFace-Modell buffalo_l (Gesichtserkennung) — NUR NICHT-KOMMERZIELLE NUTZUNG\n" +
        "  Nicht Teil von Lumora: wird erst nach Bestätigung vom Benutzer selbst heruntergeladen\n" +
        "  (Einstellungen › Gesichter) und weder im Quellcode noch in der portablen Version mitgeliefert.\n\n" +
        "OpenStreetMap-Kartenkacheln (Seite „Orte“) — Daten © OpenStreetMap-Mitwirkende, Open Database License (ODbL)\n" +
        "  Kacheln von tile.openstreetmap.org nach den Kachel-Nutzungsbedingungen der OSM Foundation\n" +
        "  (operations.osmfoundation.org/policies/tiles): eindeutiger User-Agent, Cache, höchstens zwei gleichzeitige\n" +
        "  Abrufe, keine Massenabrufe; Zuordnung sichtbar auf der Karte. Die Karte selbst ist eine eigene Kachelkarte\n" +
        "  in Lumora (kein Mapsui, kein WebView2).\n\n" +
        "Ortsnamen: GeoNames (geonames.org) — Creative Commons Namensnennung 4.0 (CC BY 4.0)\n" +
        "  Datensatz „cities5000“ samt Länder- und Regionsnamen, verdichtet und in Lumora eingebettet\n" +
        "  (offline, keine Abfrage im Netz); deutsche Namen teils ergänzt. https://creativecommons.org/licenses/by/4.0/\n\n" +
        "FFmpeg (Video-Vorschaubilder) — LGPL/GPL je nach Build\n" +
        "  Nicht Teil von Lumora: wird, falls installiert (z. B. „winget install Gyan.FFmpeg“), als externes\n" +
        "  Programm aufgerufen.";
}
