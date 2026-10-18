using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Services.Sorter;
using PhotoViewerV3.Tests.Fixtures;
using static PhotoViewerV3.Tests.Sorter.SorterTestHelper;

namespace PhotoViewerV3.Tests.Sorter;

public sealed class SorterProfileStoreTests
{
    private const string V2PatternsJson = """
        [
          {
            "Name": "Prefix",
            "Regex": "^([a-zA-Z]{1,2}\\d{2,3})",
            "GroupCapture": 1,
            "Priority": 10,
            "Enabled": true,
            "BuiltIn": true,
            "Description": "1-2 Buchstaben + 2-3 Ziffern (z.B. p006, pa09, pa170)"
          },
          {
            "Name": "Datum_YYYY-MM-DD",
            "Regex": "(\\d{4}-\\d{2}-\\d{2})",
            "GroupCapture": 1,
            "Priority": 20,
            "Enabled": false,
            "BuiltIn": true,
            "Description": "Datum im Format YYYY-MM-DD (z.B. 2024-01-15)"
          },
          {
            "Name": "DSCF",
            "Regex": "^(DSCF)",
            "GroupCapture": 1,
            "Priority": 5,
            "Enabled": true,
            "BuiltIn": false,
            "Description": ""
          }
        ]
        """;

    /// <summary>V2 schreibt ein einzelnes Sub-Level als Objekt statt Array (ConvertTo-Json).</summary>
    private const string V2SubLevelsJson = """
        {
          "Name": "Variante",
          "Regex": "_1_(result(?:_\\d+)?)\\.",
          "GroupCapture": 1,
          "Enabled": false
        }
        """;

    [Fact]
    public async Task Defaults_wie_V2_wenn_nichts_gespeichert_ist()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);

        var patterns = await s.Store.GetPatternsAsync();
        var subLevels = await s.Store.GetSubLevelsAsync();

        patterns.Select(p => p.Name).Should().Equal("Prefix", "Datum_YYYY-MM-DD", "Datum_YYYYMMDD");
        patterns.Should().OnlyContain(p => p.BuiltIn);
        patterns[0].Regex.Should().Be(@"^([a-zA-Z]{1,2}\d{2,3})");
        patterns[0].Enabled.Should().BeTrue();
        patterns[1].Enabled.Should().BeFalse();
        patterns[2].Enabled.Should().BeFalse();
        patterns.Select(p => p.Priority).Should().Equal(10, 20, 30);
        subLevels.Should().ContainSingle().Which.Name.Should().Be("Variante");
    }

    [Fact]
    public async Task Speichern_und_Laden_ueber_die_Settings_Tabelle()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var patterns = SorterProfileStore.CreateDefaultPatterns();
        patterns[0].Enabled = false;
        patterns.Add(Pattern("WhatsApp", @"^IMG-(\d{8})-WA", priority: 5));

        await s.Store.SavePatternsAsync(patterns);
        await s.Store.SaveSubLevelsAsync([SubLevel("Ebene", "(a)"), SubLevel("Zwei", "(b)", enabled: false)]);

        var loaded = await s.Store.GetPatternsAsync();
        loaded.Should().HaveCount(4);
        loaded[0].Enabled.Should().BeFalse();
        loaded[3].Name.Should().Be("WhatsApp");
        loaded[3].Regex.Should().Be(@"^IMG-(\d{8})-WA");
        loaded[3].Priority.Should().Be(5);
        loaded[3].BuiltIn.Should().BeFalse();

        var levels = await s.Store.GetSubLevelsAsync();
        levels.Select(l => l.Name).Should().Equal("Ebene", "Zwei");
        levels[1].Enabled.Should().BeFalse();

        await using var ctx = fx.CreateContext();
        var keys = await ctx.Settings.Select(x => x.Key).ToListAsync();
        keys.Should().Contain(SorterProfileStore.PatternsKey).And.Contain(SorterProfileStore.SubLevelsKey);
        (await ctx.Settings.SingleAsync(x => x.Key == SorterProfileStore.PatternsKey)).Value.Should().Contain("\"WhatsApp\"");
    }

    [Fact]
    public async Task Speichern_validiert_Regex_Namen_und_Capture_Gruppe()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);

        var invalidRegex = () => s.Store.SavePatternsAsync([Pattern("Kaputt", "(unclosed")]);
        var duplicate = () => s.Store.SavePatternsAsync([Pattern("A", "(x)"), Pattern("a", "(y)")]);
        var capture = () => s.Store.SavePatternsAsync([Pattern("Zuviel", "(x)", capture: 2)]);
        var noName = () => s.Store.SavePatternsAsync([Pattern(" ", "(x)")]);

        (await invalidRegex.Should().ThrowAsync<SorterProfileException>()).Which.Errors.Should().ContainSingle().Which.Should().Contain("Kaputt").And.Contain("ungültiger Regex");
        (await duplicate.Should().ThrowAsync<SorterProfileException>()).Which.Errors.Should().ContainSingle().Which.Should().Contain("doppelt");
        (await capture.Should().ThrowAsync<SorterProfileException>()).Which.Errors.Should().ContainSingle().Which.Should().Contain("Gruppe 2");
        (await noName.Should().ThrowAsync<SorterProfileException>()).Which.Errors.Should().ContainSingle().Which.Should().Contain("Name fehlt");

        s.Store.ValidatePatterns([Pattern("Ganz", "IMG", capture: 0)]).Should().BeEmpty("Capture 0 braucht keine Gruppe");
        s.Store.ValidateSubLevels([SubLevel("", "(a)")]).Should().BeEmpty("Sub-Level-Namen sind optional");
        (await s.Store.GetPatternsAsync()).Should().HaveCount(3, "fehlgeschlagene Speicherungen ändern nichts");
    }

    [Fact]
    public async Task Import_liest_V2_Pattern_JSON()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var path = Path.Combine(fx.RootDir, "file-sorter-patterns.json");
        await File.WriteAllTextAsync(path, V2PatternsJson);

        var imported = await s.Store.ImportPatternsAsync(path);

        imported.Select(p => p.Name).Should().Equal("Prefix", "Datum_YYYY-MM-DD", "DSCF");
        var dscf = imported.Single(p => p.Name == "DSCF");
        dscf.Regex.Should().Be("^(DSCF)");
        dscf.Priority.Should().Be(5);
        dscf.BuiltIn.Should().BeFalse();
        dscf.Description.Should().BeEmpty();
        (await s.Store.GetPatternsAsync()).Should().HaveCount(3, "Import speichert sofort");
    }

    [Fact]
    public async Task Import_liest_V2_SubLevel_JSON_als_einzelnes_Objekt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var path = Path.Combine(fx.RootDir, "file-sorter-sublevels.json");
        await File.WriteAllTextAsync(path, V2SubLevelsJson);

        var imported = await s.Store.ImportSubLevelsAsync(path);

        var level = imported.Should().ContainSingle().Subject;
        level.Name.Should().Be("Variante");
        level.Regex.Should().Be(@"_1_(result(?:_\d+)?)\.");
        level.GroupCapture.Should().Be(1);
        level.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Import_lehnt_kaputte_Dateien_mit_Fehlermeldung_ab()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var path = Path.Combine(fx.RootDir, "kaputt.json");
        await File.WriteAllTextAsync(path, "{ not json");

        var act = () => s.Store.ImportPatternsAsync(path);

        (await act.Should().ThrowAsync<SorterProfileException>()).Which.Errors.Should().ContainSingle().Which.Should().Contain("kein gültiges Pattern-JSON");
    }

    [Fact]
    public async Task Export_und_Import_sind_ein_Roundtrip_im_V2_Format()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        await s.Store.SavePatternsAsync([Pattern("Scan", @"^(Scan)[_ -]", priority: 5), Pattern("Datum", @"(\d{4}-\d{2}-\d{2})", priority: 20, enabled: false)]);
        var path = Path.Combine(fx.RootDir, "export", "patterns.json");

        await s.Store.ExportPatternsAsync(path);
        var json = await File.ReadAllTextAsync(path);
        await s.Store.ResetPatternsAsync();
        var reimported = await s.Store.ImportPatternsAsync(path);

        json.Should().Contain("\"Name\": \"Scan\"").And.Contain("\"Regex\": \"^(Scan)[_ -]\"").And.Contain("\"GroupCapture\": 1");
        reimported.Select(p => (p.Name, p.Regex, p.Priority, p.Enabled)).Should().Equal(
            ("Scan", @"^(Scan)[_ -]", 5, true),
            ("Datum", @"(\d{4}-\d{2}-\d{2})", 20, false));
    }

    [Fact]
    public async Task Reset_stellt_die_Defaults_wieder_her()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        await s.Store.SavePatternsAsync([Pattern("Nur eins", "(x)")]);
        await s.Store.SaveSubLevelsAsync([]);

        var patterns = await s.Store.ResetPatternsAsync();
        var levels = await s.Store.ResetSubLevelsAsync();

        patterns.Select(p => p.Name).Should().Equal("Prefix", "Datum_YYYY-MM-DD", "Datum_YYYYMMDD");
        (await s.Store.GetPatternsAsync()).Should().HaveCount(3);
        levels.Should().ContainSingle().Which.Name.Should().Be("Variante");
        (await s.Store.GetSubLevelsAsync()).Should().ContainSingle();
    }
}
