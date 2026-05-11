using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PhotoViewerV3.Data;

/// <summary>
/// Wird von 'dotnet ef' beim Erzeugen von Migrations / SQL-Scripts aufgerufen.
/// Verwendet einen Design-Time-spezifischen DB-Pfad — die Production-DB wird
/// durch den Application-Bootstrap (App.xaml.cs + DI) konfiguriert.
/// </summary>
public sealed class DesignTimeLibraryContextFactory : IDesignTimeDbContextFactory<LibraryContext>
{
    public LibraryContext CreateDbContext(string[] args)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(localAppData, "PhotoViewerV3", "library.db");

        var optionsBuilder = new DbContextOptionsBuilder<LibraryContext>();
        optionsBuilder.UseSqlite($"Data Source={dbPath}");

        return new LibraryContext(optionsBuilder.Options);
    }
}
