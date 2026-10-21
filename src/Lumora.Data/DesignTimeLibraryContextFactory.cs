using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lumora.Data;

/// <summary>
/// Wird von 'dotnet ef' beim Erzeugen von Migrations / SQL-Scripts aufgerufen.
/// Verwendet eine Wegwerf-DB im Temp-Ordner — NIE den echten Katalog unter %LocalAppData%\Lumora
/// (0.2.50: 'dotnet ef migrations remove --force' hätte sonst den Benutzer-Katalog öffnen können).
/// Die Production-DB wird durch den Application-Bootstrap (App.xaml.cs + DI) konfiguriert.
/// </summary>
public sealed class DesignTimeLibraryContextFactory : IDesignTimeDbContextFactory<LibraryContext>
{
    public LibraryContext CreateDbContext(string[] args)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "Lumora-DesignTime", "design-time.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var optionsBuilder = new DbContextOptionsBuilder<LibraryContext>();
        optionsBuilder.UseSqlite($"Data Source={dbPath}");

        return new LibraryContext(optionsBuilder.Options);
    }
}
