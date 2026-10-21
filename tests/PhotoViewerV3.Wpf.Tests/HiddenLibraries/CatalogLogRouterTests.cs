using System.IO;
using PhotoViewerV3.Shell.HiddenLibraries;
using Serilog.Events;

namespace PhotoViewerV3.Wpf.Tests.HiddenLibraries;

/// <summary>
/// ADR-0049: Protokolle der versteckten Sitzung nur im Container; Nachzügler nach dem Zusperren landen nirgends;
/// Absturzberichte nach einer versteckten Sitzung ohne Details im normalen Protokoll.
/// </summary>
public sealed class CatalogLogRouterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Wpf.Tests", "logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Hidden_session_logs_only_into_the_container_and_late_writes_are_dropped()
    {
        var normalLogs = Path.Combine(_root, "appdata", "logs");
        var hiddenLogs = Path.Combine(_root, "container", ".lumora", "logs");
        var normal = CatalogLoggers.CreateNormal(normalLogs);
        var router = new CatalogLogRouter(normal);
        var global = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(router).CreateLogger();

        global.Information("normal vorher");
        var hidden = CatalogLoggers.CreateHidden(hiddenLogs);
        router.RouteToHidden(hidden);
        global.Information("Datei {Name} im Tresor", "geheim-urlaub.jpg");
        hidden.Information("Dienst der versteckten Sitzung: {Path}", @"X:\Tresor\geheim.jpg");
        router.RouteToNormal(normal);
        hidden.Dispose();
        hidden.Information("Nachzügler {Path}", @"X:\Tresor\spaet.jpg"); // Hintergrund-Task nach dem Zusperren
        global.Information("normal nachher");
        global.Dispose();
        normal.Dispose();

        var normalText = ReadAll(normalLogs);
        normalText.Should().Contain("normal vorher").And.Contain("normal nachher");
        normalText.Should().NotContain("geheim").And.NotContain("Tresor").And.NotContain("spaet");
        var hiddenText = ReadAll(hiddenLogs);
        hiddenText.Should().Contain("geheim-urlaub.jpg").And.Contain(@"X:\Tresor\geheim.jpg").And.NotContain("spaet");
        Directory.EnumerateFiles(hiddenLogs).Should().OnlyContain(f => Path.GetFileName(f).StartsWith("lumora-", StringComparison.Ordinal));
    }

    [Fact]
    public void Crash_reports_after_a_hidden_session_carry_no_details()
    {
        var normalLogs = Path.Combine(_root, "appdata", "logs");
        var normal = CatalogLoggers.CreateNormal(normalLogs);
        var router = new CatalogLogRouter(normal);
        var exception = new IOException(@"X:\Tresor\geheim.jpg ist gesperrt");

        router.WriteCrash(LogEventLevel.Error, exception, "vorher");
        var hidden = CatalogLoggers.CreateHidden(Path.Combine(_root, "container", ".lumora", "logs"));
        router.RouteToHidden(hidden);
        router.WriteCrash(LogEventLevel.Error, exception, "während");
        router.RouteToNormal(normal);
        hidden.Dispose();
        router.WriteCrash(LogEventLevel.Error, exception, "nachher");
        normal.Dispose();

        router.HiddenSessionWasOpened.Should().BeTrue();
        var lines = File.ReadAllLines(Directory.EnumerateFiles(normalLogs).Single());
        lines.Should().Contain(l => l.Contains("vorher")); // vor jeder versteckten Sitzung: vollständig
        var after = string.Join("\n", lines.SkipWhile(l => !l.Contains("nachher")));
        after.Should().Contain("System.IO.IOException").And.Contain("Details unterdrückt").And.NotContain("Tresor");
        File.ReadAllText(Directory.EnumerateFiles(normalLogs).Single()).Should().NotContain("während");
    }

    private static string ReadAll(string folder)
        => string.Join("\n", Directory.EnumerateFiles(folder).Select(f =>
        {
            using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
}
