using System.Globalization;
using System.Windows.Data;
using Lumora.Albums;

namespace Lumora.Wpf.Tests.Albums;

/// <summary>
/// Schalter „mit Verknüpfungen“/„nur echte Dateien“ (ADR-0054): nur das Anhaken schreibt zurück. Mit einem Umkehr-Konverter
/// schaukelten sich die Optionsfelder der beiden Galerie-Instanzen bis zum Stapelüberlauf auf (Rauchtest 0.3.2).
/// </summary>
public sealed class BoolOptionConverterTests
{
    private static readonly BoolOptionConverter Converter = new();

    [Theory]
    [InlineData(true, "True", true)]
    [InlineData(true, "False", false)]
    [InlineData(false, "False", true)]
    [InlineData(false, "True", false)]
    public void Angehakt_wenn_Wert_dem_Parameter_entspricht(bool value, string parameter, bool expected)
        => Converter.Convert(value, typeof(bool), parameter, CultureInfo.InvariantCulture).Should().Be(expected);

    [Fact]
    public void Nur_Anhaken_schreibt_zurueck()
    {
        Converter.ConvertBack(true, typeof(bool), "False", CultureInfo.InvariantCulture).Should().Be(false);
        Converter.ConvertBack(true, typeof(bool), "True", CultureInfo.InvariantCulture).Should().Be(true);
        Converter.ConvertBack(false, typeof(bool), "True", CultureInfo.InvariantCulture).Should().BeSameAs(Binding.DoNothing);
        Converter.ConvertBack(false, typeof(bool), "False", CultureInfo.InvariantCulture).Should().BeSameAs(Binding.DoNothing);
    }
}
