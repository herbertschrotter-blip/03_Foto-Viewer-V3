using PhotoViewerV3.Gallery.Data;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// <see cref="GalleryQuery.HasSearch"/> folgt dem <c>FtsQueryBuilder</c> (Data/RawSql, dort auch gegen echtes FTS5
/// getestet): nur Eingaben mit suchbarem Inhalt lösen einen FTS-MATCH aus; leerer Text oder reine Sonderzeichen
/// (- : ( ) " *) bedeuten „keine Suche“ statt eines FTS5-Syntaxfehlers oder einer leeren Phrase.
/// </summary>
public sealed class GalleryQueryTests
{
    [Theory]
    [InlineData("IMG")]
    [InlineData("  urlaub   2024 ")]
    [InlineData("foto-2024")]
    [InlineData("a\"b")]
    [InlineData("(x):y*")]
    public void HasSearch_ist_wahr_bei_suchbarem_Inhalt(string term)
        => new GalleryQuery(1, GalleryFilter.All, term).HasSearch.Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("\"\"")]
    [InlineData("* ( )")]
    public void HasSearch_ist_falsch_ohne_suchbaren_Inhalt(string? term)
        => new GalleryQuery(1, GalleryFilter.All, term).HasSearch.Should().BeFalse();

    [Fact]
    public void MatchExpression_ist_der_Praefix_Ausdruck_des_FtsQueryBuilders()
        => new GalleryQuery(1, GalleryFilter.All, "foto-2024 x").MatchExpression.Should().Be("\"foto-2024\"* \"x\"*");
}
