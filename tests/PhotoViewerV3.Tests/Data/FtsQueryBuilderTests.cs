using Microsoft.Data.Sqlite;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Data;

/// <summary>
/// <see cref="FtsQueryBuilder"/>: Benutzereingaben wie <c>foo-bar</c>, <c>"</c>, <c>*</c> oder <c>AND</c>
/// dürfen nie zu „fts5: syntax error“ führen, und die Präfix-Suche muss die erwarteten Dateien finden.
/// </summary>
public sealed class FtsQueryBuilderTests
{
    [Theory]
    [InlineData("foo-bar", "\"foo-bar\"*")]
    [InlineData("  a   b ", "\"a\"* \"b\"*")]
    [InlineData("AND", "\"AND\"*")]
    [InlineData("foo AND bar", "\"foo\"* \"AND\"* \"bar\"*")]
    [InlineData("a\"b", "\"a\"\"b\"*")]
    [InlineData("IMG_20", "\"IMG_20\"*")]
    [InlineData("(1)", "\"(1)\"*")]
    [InlineData("Ärger ✨", "\"Ärger\"*")]
    public void FromUserInput_setzt_jedes_Wort_als_Praefix_Phrase_in_Anfuehrungszeichen(string input, string expected)
    {
        FtsQueryBuilder.FromUserInput(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("\"")]
    [InlineData("- ( ) * \"\" :")]
    public void FromUserInput_liefert_null_ohne_suchbaren_Inhalt(string? input)
    {
        FtsQueryBuilder.FromUserInput(input).Should().BeNull();
    }

    [Fact]
    public void FromUserInput_ohne_Praefix_liefert_reine_Phrasen()
    {
        FtsQueryBuilder.FromUserInput("foo bar", prefix: false).Should().Be("\"foo\" \"bar\"");
    }

    [Theory]
    [InlineData("foo-bar", "foo-bar.jpg")]
    [InlineData("foo", "foo-bar.jpg|foo.png")]
    [InlineData("IMG_20", "IMG_2024 (1).jpg")]
    [InlineData("(1)", "IMG_2024 (1).jpg")]
    [InlineData("ärg", "Ärger.jpg")]
    [InlineData("AND", "and.mp4")]
    [InlineData("png", "foo.png")]
    [InlineData("foo AND bar", "")]
    [InlineData("NOT foo", "")]
    [InlineData("foo*", "foo-bar.jpg|foo.png")]
    [InlineData("\"foo", "foo-bar.jpg|foo.png")]
    [InlineData("-", "")]
    [InlineData("*", "")]
    [InlineData("\"", "")]
    public async Task MATCH_mit_FromUserInput_laeuft_ohne_Syntaxfehler_und_findet_die_erwarteten_Dateien(string input, string expectedNames)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        foreach (var name in new[] { "foo-bar.jpg", "IMG_2024 (1).jpg", "Ärger.jpg", "and.mp4", "foo.png" })
        {
            fx.WriteLibraryFile(name);
        }

        await fx.ScanAsync(library.Id);
        var expected = expectedNames.Split('|', StringSplitOptions.RemoveEmptyEntries);

        var names = await SearchNamesAsync(fx, input);

        names.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData("foo-bar")] // FTS5 liest „foo - bar“ → "no such column: bar"
    [InlineData("\"")]      // offene Phrase → "fts5: syntax error"
    [InlineData("foo AND")] // Operator ohne rechten Operanden
    public async Task Rohe_Eingabe_fuehrt_ohne_Builder_zu_einer_SqliteException(string rawInput)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("foo-bar.jpg");
        await fx.ScanAsync(library.Id);

        var raw = () => RunMatch(fx, rawInput);

        raw.Should().Throw<SqliteException>("die Galerie darf Benutzereingaben nie roh an MATCH übergeben");
    }

    [Fact]
    public async Task FTS_delete_fuer_nicht_indexierte_Zeile_wirft_nicht_und_der_Index_bleibt_abfragbar()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("alpha.jpg");
        await fx.ScanAsync(library.Id);
        var alpha = (await fx.FindFileAsync("alpha.jpg"))!;

        using (var connection = fx.ContextFactory.OpenSqliteConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO FilesFts(FilesFts, rowid, Name, Extension) VALUES('delete', 999, 'ghost.jpg', '.jpg');";
            var act = () => cmd.ExecuteNonQuery();
            act.Should().NotThrow("FTS5 'delete' für eine nie indexierte rowid ist ein No-Op auf den Doclists");
        }

        fx.FtsRowIds("alpha").Should().Equal(alpha.Id);
        fx.FtsRowIds("ghost").Should().BeEmpty();
    }

    private static async Task<List<string>> SearchNamesAsync(TempLibraryFixture fx, string input)
    {
        var term = FtsQueryBuilder.FromUserInput(input);
        if (term is null)
        {
            return new List<string>();
        }

        var ids = RunMatch(fx, term);
        var names = new List<string>();
        foreach (var id in ids)
        {
            names.Add((await fx.FindFileByIdAsync(id))!.Name);
        }

        return names;
    }

    private static List<int> RunMatch(TempLibraryFixture fx, string term)
    {
        using var connection = fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rowid FROM FilesFts WHERE FilesFts MATCH $term";
        cmd.Parameters.AddWithValue("$term", term);
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
        {
            ids.Add((int)reader.GetInt64(0));
        }

        return ids;
    }
}
