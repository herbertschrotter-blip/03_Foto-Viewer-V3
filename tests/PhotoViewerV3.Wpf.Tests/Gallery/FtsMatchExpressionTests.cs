using PhotoViewerV3.Spike02.Data;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>Der Suchtext der Galerie darf nie als roher FTS5-Ausdruck ankommen (Syntaxfehler bei - : ( ) " *).</summary>
public sealed class FtsMatchExpressionTests
{
    [Theory]
    [InlineData("IMG", "\"IMG\"*")]
    [InlineData("  urlaub   2024 ", "\"urlaub\"* \"2024\"*")]
    [InlineData("foto-2024", "\"foto-2024\"*")]
    [InlineData("a\"b", "\"a\"\"b\"*")]
    [InlineData("(x):y*", "\"(x):y*\"*")]
    public void BuildsQuotedPrefixPhrases(string term, string expected)
        => GalleryPageSource.BuildFtsMatchExpression(term).Should().Be(expected);

    [Theory]
    [InlineData("---")]
    [InlineData("\"\"")]
    [InlineData("* ( )")]
    public void OnlyPunctuation_YieldsEmptyPhrase(string term)
        => GalleryPageSource.BuildFtsMatchExpression(term).Should().Be("\"\"");
}
