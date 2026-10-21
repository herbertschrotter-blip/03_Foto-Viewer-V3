namespace Lumora.Services.Scanning;

/// <summary>
/// Interner Kontrollfluss des Scans: der Bibliotheks-Root ist (nicht mehr) erreichbar. Wird vom ScanService in
/// ein <see cref="ScanResult"/> mit <see cref="ScanOutcome.LibraryOffline"/> übersetzt und verlässt den Service nie.
/// </summary>
internal sealed class LibraryOfflineException : Exception
{
    public LibraryOfflineException(string rootPath)
        : base(Describe(rootPath))
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }

    /// <summary>Benutzertext gemäß PLAN Kap. 12 („UI bei Storage offline“).</summary>
    public static string Describe(string rootPath) =>
        $"Bibliothek offline: {rootPath} ist nicht erreichbar. Bitte Laufwerk anschließen oder Netzwerkverbindung prüfen.";
}
