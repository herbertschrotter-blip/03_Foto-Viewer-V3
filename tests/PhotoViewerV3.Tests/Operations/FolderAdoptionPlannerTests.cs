using PhotoViewerV3.Services.Operations;

namespace PhotoViewerV3.Tests.Operations;

/// <summary>
/// Reine Plan-Logik von „Ordner in die Bibliothek übernehmen“ (ADR-0034): Leerketten zusammenziehen, Verzweigungen,
/// Dateien im Zwischenordner, Wurzel als Durchgangsordner, leere Blätter, Namenskonflikte, Zusammenführen in vorhandene
/// Ordner, Groß-/Kleinschreibung — ohne Dateisystem (Fake-Probe).
/// </summary>
public sealed class FolderAdoptionPlannerTests
{
    private static readonly string Src = @"C:\quelle";
    private static readonly string Ziel = @"C:\bibliothek\2024";

    private static AdoptionSourceSnapshot Snapshot(AdoptionSourceFolder root) => new(root, Array.Empty<string>(), Array.Empty<string>());

    private static AdoptionSourceFolder Folder(string name) => new(Path.Combine(Src, name));

    private static string T(params string[] parts) => Path.Combine(new[] { Ziel }.Concat(parts).ToArray());

    private static Dictionary<string, string> DestBySource(FolderAdoptionPlan plan)
        => plan.Files.ToDictionary(f => Path.GetRelativePath(Src, f.SourcePath), f => f.DestPath, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Beispielkette_Fotos_leer_leer_leer_inhalt_wird_Fotos_inhalt()
    {
        var fotos = Folder("Fotos").WithFiles("a.jpg");
        fotos.AddChild("leer").AddChild("leer").AddChild("leer").AddChild("inhalt").WithFiles("b.jpg");

        var plan = FolderAdoptionPlanner.Plan(Snapshot(fotos), Ziel, collapseEmptyChains: true, new FakeProbe());

        DestBySource(plan).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [@"Fotos\a.jpg"] = T("Fotos", "a.jpg"),
            [@"Fotos\leer\leer\leer\inhalt\b.jpg"] = T("Fotos", "inhalt", "b.jpg"),
        });
        plan.Folders.Select(f => f.RelativeTargetPath).Should().Equal("Fotos", @"Fotos\inhalt");
        plan.CollapsedFolders.Should().HaveCount(3, "drei Durchgangsordner entfallen");
        plan.Folders[1].CollapsedPaths.Should().HaveCount(3);
        plan.Folders[1].Depth.Should().Be(2);
    }

    [Fact]
    public void Wurzel_selbst_ist_Durchgangsordner()
    {
        var import = Folder("Import");
        import.AddChild("leer").AddChild("inhalt").WithFiles("x.jpg");

        var plan = FolderAdoptionPlanner.Plan(Snapshot(import), Ziel, true, new FakeProbe());

        plan.Files.Should().ContainSingle().Which.DestPath.Should().Be(T("inhalt", "x.jpg"));
        plan.Folders.Should().ContainSingle().Which.RelativeTargetPath.Should().Be("inhalt");
        plan.CollapsedFolders.Should().Equal(Path.Combine(Src, "Import"), Path.Combine(Src, "Import", "leer"));
    }

    [Fact]
    public void Verzweigung_bleibt_vollstaendig_unter_dem_gemeinsamen_Ordner()
    {
        var x = Folder("X");
        x.AddChild("A").AddChild("leer").AddChild("tief").WithFiles("a.jpg");
        x.AddChild("B").WithFiles("b.jpg");

        var plan = FolderAdoptionPlanner.Plan(Snapshot(x), Ziel, true, new FakeProbe());

        DestBySource(plan).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [@"X\A\leer\tief\a.jpg"] = T("X", "tief", "a.jpg"),
            [@"X\B\b.jpg"] = T("X", "B", "b.jpg"),
        });
        plan.Folders.Select(f => f.RelativeTargetPath).Should().Equal("X", @"X\B", @"X\tief");
    }

    [Fact]
    public void Datei_im_Zwischenordner_haelt_ihn_fest()
    {
        var root = Folder("R").WithFiles("r.jpg");
        root.AddChild("mid").WithFiles("m.jpg").AddChild("leer").AddChild("inhalt").WithFiles("i.jpg");

        var plan = FolderAdoptionPlanner.Plan(Snapshot(root), Ziel, true, new FakeProbe());

        DestBySource(plan).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [@"R\r.jpg"] = T("R", "r.jpg"),
            [@"R\mid\m.jpg"] = T("R", "mid", "m.jpg"),
            [@"R\mid\leer\inhalt\i.jpg"] = T("R", "mid", "inhalt", "i.jpg"),
        });
    }

    [Fact]
    public void Leere_Blaetter_und_leere_Teilbaeume_werden_nicht_angelegt_und_zaehlen_nicht_als_Unterordner()
    {
        var root = Folder("R");
        root.AddChild("leer1");
        root.AddChild("leer2").AddChild("leer3");
        root.AddChild("voll").WithFiles("v.jpg");

        var plan = FolderAdoptionPlanner.Plan(Snapshot(root), Ziel, true, new FakeProbe());

        plan.Files.Should().ContainSingle().Which.DestPath.Should().Be(T("voll", "v.jpg"),
            "R hat nur einen nicht leeren Unterordner und ist damit Durchgangsordner");
        plan.Folders.Should().ContainSingle();

        var ohne = FolderAdoptionPlanner.Plan(Snapshot(root), Ziel, collapseEmptyChains: false, new FakeProbe());
        ohne.Folders.Select(f => f.RelativeTargetPath).Should().Equal("R", @"R\voll");
    }

    [Fact]
    public void Ohne_Zusammenziehen_bleibt_die_Kette_erhalten()
    {
        var import = Folder("Import");
        import.AddChild("leer").AddChild("inhalt").WithFiles("x.jpg");

        var plan = FolderAdoptionPlanner.Plan(Snapshot(import), Ziel, collapseEmptyChains: false, new FakeProbe());

        plan.Files.Single().DestPath.Should().Be(T("Import", "leer", "inhalt", "x.jpg"));
        plan.CollapsedFolders.Should().BeEmpty();
    }

    [Fact]
    public void Namenskonflikte_bekommen_Suffix_1_2_auch_ohne_Gross_Kleinschreibung()
    {
        var root = Folder("R").WithFiles("a.jpg", "b.jpg");
        var probe = new FakeProbe();
        probe.Dirs.Add(T("R"));
        probe.Files.Add(T("R", "A.JPG"));
        probe.Files.Add(T("R", "a_1.jpg"));

        var plan = FolderAdoptionPlanner.Plan(Snapshot(root), Ziel, true, probe);

        var a = plan.Files.Single(f => f.SourcePath.EndsWith("a.jpg", StringComparison.Ordinal));
        a.DestPath.Should().Be(T("R", "a_2.jpg"));
        a.Renamed.Should().BeTrue();
        plan.Files.Single(f => f.SourcePath.EndsWith("b.jpg", StringComparison.Ordinal)).Renamed.Should().BeFalse();
        plan.RenamedCount.Should().Be(1);
        plan.Folders.Single().MergesExisting.Should().BeTrue();
    }

    [Fact]
    public void Vorhandener_Zielordner_wird_mit_seinem_Namen_zusammengefuehrt()
    {
        var import = Folder("Import");
        import.AddChild("leer").AddChild("inhalt").WithFiles("x.jpg");
        var probe = new FakeProbe();
        probe.Dirs.Add(T("Inhalt"));

        var plan = FolderAdoptionPlanner.Plan(Snapshot(import), Ziel, true, probe);

        plan.Files.Single().DestPath.Should().Be(T("Inhalt", "x.jpg"), "vorhandene Schreibweise gewinnt");
        plan.Folders.Single().MergesExisting.Should().BeTrue();
        plan.MergedFolderCount.Should().Be(1);
    }

    [Fact]
    public void Geschwister_die_nach_dem_Zusammenziehen_gleich_hiessen_bleiben_unterscheidbar()
    {
        var root = Folder("R");
        root.AddChild("A").AddChild("Bilder").WithFiles("a.jpg");
        root.AddChild("B").AddChild("bilder").WithFiles("b.jpg");

        var plan = FolderAdoptionPlanner.Plan(Snapshot(root), Ziel, true, new FakeProbe());

        DestBySource(plan).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [@"R\A\Bilder\a.jpg"] = T("R", "A", "Bilder", "a.jpg"),
            [@"R\B\bilder\b.jpg"] = T("R", "B", "bilder", "b.jpg"),
        });
    }

    [Fact]
    public void Datei_mit_dem_Ordnernamen_im_Ziel_fuehrt_zu_Ordner_mit_Suffix()
    {
        var import = Folder("Import");
        import.AddChild("leer").AddChild("inhalt").WithFiles("x.jpg");
        var probe = new FakeProbe();
        probe.Files.Add(T("inhalt"));

        var plan = FolderAdoptionPlanner.Plan(Snapshot(import), Ziel, true, probe);

        plan.Files.Single().DestPath.Should().Be(T("inhalt_1", "x.jpg"));
    }

    [Fact]
    public void Ordner_ohne_Dateien_ergibt_leeren_Plan_und_Ziel_im_Quellordner_wird_abgelehnt()
    {
        var root = Folder("R");
        root.AddChild("leer");

        FolderAdoptionPlanner.Plan(Snapshot(root), Ziel, true, new FakeProbe()).IsEmpty.Should().BeTrue();

        var act = () => FolderAdoptionPlanner.Plan(Snapshot(root), Path.Combine(Src, "R", "unten"), true, new FakeProbe());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Quellbaum_lesen_nimmt_Nicht_Medien_mit_und_ignoriert_Systemordner_und_Systemdateien()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Tests", Guid.NewGuid().ToString("N"), "Quelle");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub", ".thumbs"));
            File.WriteAllText(Path.Combine(dir, "sub", "a.jpg"), "x");
            File.WriteAllText(Path.Combine(dir, "sub", "a.xmp"), "x");
            File.WriteAllText(Path.Combine(dir, "sub", ".thumbs", "t.jpg"), "x");
            File.WriteAllText(Path.Combine(dir, "Thumbs.db"), "x");

            var snapshot = FolderAdoptionSourceReader.Read(dir);

            snapshot.Root.TotalFileCount.Should().Be(2);
            snapshot.Root.Children.Single().Files.Should().BeEquivalentTo("a.jpg", "a.xmp");
            snapshot.IgnoredEntries.Select(Path.GetFileName).Should().BeEquivalentTo(".thumbs", "Thumbs.db");
            FolderAdoptionSourceReader.FindRemainingFiles(dir).Should().HaveCount(2);
            File.Delete(Path.Combine(dir, "sub", "a.jpg"));
            File.Delete(Path.Combine(dir, "sub", "a.xmp"));
            FolderAdoptionSourceReader.FindRemainingFiles(dir).Should().BeEmpty("Systemordner und Thumbs.db zählen nicht als Rest");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    private sealed class FakeProbe : IAdoptionTargetProbe
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Files.Contains(path);

        public string? FindExistingDirectoryName(string parent, string name)
        {
            var hit = Dirs.FirstOrDefault(d => string.Equals(d, Path.Combine(parent, name), StringComparison.OrdinalIgnoreCase));
            return hit is null ? null : Path.GetFileName(hit);
        }
    }
}
