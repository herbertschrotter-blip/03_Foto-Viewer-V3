using PhotoViewerV3.Services.Sorter;
using static PhotoViewerV3.Tests.Sorter.SorterTestHelper;

namespace PhotoViewerV3.Tests.Sorter;

public sealed class SorterPatternEngineTests
{
    private static readonly SorterPattern s_prefix = Pattern("Prefix", @"^([a-zA-Z]{1,2}\d{2,3})", priority: 10);

    private static List<SorterGroup> Group(IEnumerable<SorterPattern> patterns, List<string>? warnings, params string[] files)
        => SorterPatternEngine.GroupFiles(files.Select(f => MakeFile(f)), SorterPatternEngine.CompilePatterns(patterns, warnings), warnings);

    [Fact]
    public void Prioritaet_niedrigere_Zahl_zuerst_erster_Treffer_gewinnt()
    {
        var patterns = new[] { s_prefix, Pattern("DSCF", "^(DSCF)", priority: 5) };

        var groups = Group(patterns, null, "DSCF0001.jpg", "p006_0042.jpg", "ds01_x.jpg");

        groups.Select(g => g.Key).Should().Equal("ds01", "dscf", "p006");
        groups.Single(g => g.Key == "dscf").PatternName.Should().Be("DSCF");
        groups.Single(g => g.Key == "ds01").PatternName.Should().Be("Prefix");
        groups.Single(g => g.Key == "p006").PatternName.Should().Be("Prefix");
    }

    [Fact]
    public void GroupCapture_0_nimmt_den_ganzen_Treffer()
    {
        var groups = Group([Pattern("WA", @"^IMG-(\d{8})", capture: 0)], null, "IMG-20240815-WA0023.jpg");

        groups.Should().ContainSingle().Which.Key.Should().Be("img-20240815");
    }

    [Fact]
    public void GroupCapture_n_nimmt_Gruppe_n()
    {
        var groups = Group([Pattern("Smartphone", @"^(IMG|PXL)_(\d{8})", capture: 2)], null, "PXL_20240815_134522.jpg", "IMG_20240815_1.jpg", "IMG_20231224_1.jpg");

        groups.Select(g => g.Key).Should().Equal("20231224", "20240815");
        groups.Single(g => g.Key == "20240815").FileCount.Should().Be(2);
    }

    [Fact]
    public void Schluessel_sind_kleingeschrieben_und_Vorschlag_entspricht_Schluessel()
    {
        var groups = Group([s_prefix], null, "P006_0001.jpg", "p006_0002.jpg");

        var group = groups.Should().ContainSingle().Subject;
        group.Key.Should().Be("p006");
        group.SuggestedFolder.Should().Be("p006");
        group.TargetFolder.Should().Be("p006");
        group.FileCount.Should().Be(2);
    }

    [Fact]
    public void Kein_Treffer_landet_in_unsorted_und_unsorted_sortiert_ans_Ende()
    {
        var groups = Group([s_prefix], null, "foto.jpg", "zz01_a.jpg", "aa01_b.jpg");

        groups.Select(g => g.Key).Should().Equal("aa01", "zz01", SorterGroup.UnsortedKey);
        var unsorted = groups.Last();
        unsorted.IsUnsorted.Should().BeTrue();
        unsorted.PatternName.Should().Be(SorterGroup.UnsortedPatternName);
        unsorted.Files.Select(f => f.Name).Should().Equal("foto.jpg");
    }

    [Fact]
    public void Ungueltiges_Pattern_wird_uebersprungen_und_gemeldet()
    {
        var warnings = new List<string>();
        var patterns = new[] { Pattern("Kaputt", "(unclosed", priority: 1), s_prefix };

        var groups = Group(patterns, warnings, "p006_0001.jpg");

        groups.Should().ContainSingle().Which.Key.Should().Be("p006");
        warnings.Should().ContainSingle().Which.Should().Contain("Kaputt").And.Contain("ungültiger Regex");
    }

    [Fact]
    public void Deaktivierte_Patterns_werden_nicht_angewendet()
    {
        var patterns = new[] { Pattern("Datum", @"(\d{4}-\d{2}-\d{2})", priority: 20, enabled: false), s_prefix };

        var groups = Group(patterns, null, "2024-08-15_urlaub.jpg");

        groups.Should().ContainSingle().Which.Key.Should().Be(SorterGroup.UnsortedKey);
    }

    [Fact]
    public void Pattern_prueft_den_Dateinamen_ohne_Endung()
    {
        var groups = Group([Pattern("Endung", @"\.(jpg)$")], null, "p006_0042.jpg");

        groups.Should().ContainSingle().Which.Key.Should().Be(SorterGroup.UnsortedKey);
    }

    [Fact]
    public void Gleicher_Schluessel_aus_verschiedenen_Patterns_wird_eine_Gruppe()
    {
        var patterns = new[] { Pattern("Kamera", "^(IMG)_", priority: 5), Pattern("Kamera2", "^(img)-", priority: 6) };

        var groups = Group(patterns, null, "IMG_1.jpg", "IMG-2.jpg");

        var group = groups.Should().ContainSingle().Subject;
        group.Key.Should().Be("img");
        group.FileCount.Should().Be(2);
    }

    [Fact]
    public void MultiLevel_teilt_jede_Gruppe_rekursiv_in_einen_Baum()
    {
        var warnings = new List<string>();
        var levels = SorterPatternEngine.CompileSubLevels(SorterProfileStore.CreateDefaultSubLevels(), warnings);
        var groups = Group([s_prefix], warnings,
            "p105_0001_1_result.jpg", "p105_0002_1_result_1.jpg", "p105_0003_1_result_1.jpg", "p105_0004.jpg", "p200_0001_1_result.jpg");

        foreach (var group in groups)
        {
            SorterPatternEngine.SplitIntoSubGroups(group, levels);
        }

        warnings.Should().BeEmpty();
        var p105 = groups.Single(g => g.Key == "p105");
        p105.HasSubGroups.Should().BeTrue();
        p105.SubGroups.Select(s => s.Key).Should().Equal("result", "result_1", SorterGroup.OtherKey);
        p105.SubGroups.Should().OnlyContain(s => s.Level == 2 && s.PatternName == "Variante");
        p105.SubGroups.Single(s => s.Key == "result_1").Files.Select(f => f.Name).Should().Equal("p105_0002_1_result_1.jpg", "p105_0003_1_result_1.jpg");
        p105.SubGroups.Single(s => s.Key == SorterGroup.OtherKey).Files.Select(f => f.Name).Should().Equal("p105_0004.jpg");
        p105.FileCount.Should().Be(4, "die Stufe-1-Gruppe behält alle Dateien");

        var p200 = groups.Single(g => g.Key == "p200");
        p200.SubGroups.Should().ContainSingle().Which.Key.Should().Be("result");
    }

    [Fact]
    public void MultiLevel_mit_zwei_Ebenen_verschachtelt_weiter()
    {
        var levels = SorterPatternEngine.CompileSubLevels(
        [
            SubLevel("Variante", @"_1_(result(?:_\d+)?)\."),
            SubLevel("BildNr", @"_(\d{4})_1_result"),
        ]);
        var group = new SorterGroup("p105", "Prefix");
        group.Files.AddRange(new[] { "p105_0001_1_result.jpg", "p105_0002_1_result.jpg", "p105_0001_1_result_1.jpg" }.Select(f => MakeFile(f)));

        SorterPatternEngine.SplitIntoSubGroups(group, levels);

        var result = group.SubGroups.Single(s => s.Key == "result");
        result.SubGroups.Select(s => s.Key).Should().Equal("0001", "0002");
        result.SubGroups.Should().OnlyContain(s => s.Level == 3 && s.PatternName == "BildNr");
    }

    [Fact]
    public void SubLevel_prueft_den_vollen_Dateinamen_mit_Endung()
    {
        var levels = SorterPatternEngine.CompileSubLevels([SubLevel("Endung", @"\.([a-z0-9]+)$")]);
        var group = new SorterGroup("p105", "Prefix");
        group.Files.AddRange(new[] { "p105_a.jpg", "p105_b.mp4", "p105_c.JPG" }.Select(f => MakeFile(f)));

        SorterPatternEngine.SplitIntoSubGroups(group, levels);

        group.SubGroups.Select(s => s.Key).Should().Equal("jpg", "mp4");
        group.SubGroups.Single(s => s.Key == "jpg").FileCount.Should().Be(2);
    }

    [Fact]
    public void Ungueltiges_SubLevel_wird_uebersprungen_und_gemeldet()
    {
        var warnings = new List<string>();

        var levels = SorterPatternEngine.CompileSubLevels([SubLevel("Kaputt", "[unclosed"), SubLevel("Ok", "(a)")], warnings);

        levels.Should().ContainSingle().Which.Name.Should().Be("Ok");
        warnings.Should().ContainSingle().Which.Should().Contain("Kaputt");
    }

    [Theory]
    [InlineData("result", "r0")]
    [InlineData("result_1", "r1")]
    [InlineData("result_12", "r12")]
    [InlineData("_other", "other")]
    [InlineData("xyz", "xyz")]
    public void ToShortSubKey_entspricht_V2(string input, string expected)
        => SorterPaths.ToShortSubKey(input).Should().Be(expected);

    [Theory]
    [InlineData("p006", true)]
    [InlineData("Set_006", true)]
    [InlineData("a/b", false)]
    [InlineData(@"a\b", false)]
    [InlineData("..", false)]
    [InlineData("a..b", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("name.", false)]
    [InlineData("na:me", false)]
    public void IsValidFolderName_blockiert_Traversal_und_ungueltige_Zeichen(string name, bool expected)
        => SorterPaths.IsValidFolderName(name, out _).Should().Be(expected);

    [Fact]
    public void SanitizeFolderName_ersetzt_ungueltige_Zeichen_und_laesst_gueltige_Schluessel_unveraendert()
    {
        SorterPaths.SanitizeFolderName("p006").Should().Be("p006");
        SorterPaths.SanitizeFolderName("a/b:c").Should().Be("a_b_c");
        SorterPaths.SanitizeFolderName("...").Should().Be("_gruppe");
    }
}
