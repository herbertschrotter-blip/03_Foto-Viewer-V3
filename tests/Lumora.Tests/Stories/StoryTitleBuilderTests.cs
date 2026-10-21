using Lumora.Domain.Enums;
using Lumora.Services.Stories;

namespace Lumora.Tests.Stories;

/// <summary>Sachliche Überschriften (ADR-0058): Datumsformen, Monats-/Jahreswechsel, ungefähre Daten, 1/2/3/&gt;3 Orte.</summary>
public sealed class StoryTitleBuilderTests
{
    [Theory]
    [InlineData("2025-09-06", "2025-09-06", CaptureDatePrecision.Exact, "Samstag, 6. September 2025")]
    [InlineData("2025-07-12", "2025-07-19", CaptureDatePrecision.Exact, "12.–19. Juli 2025")]
    [InlineData("2024-07-28", "2024-08-09", CaptureDatePrecision.Exact, "28. Juli – 9. August 2024")]
    [InlineData("2024-12-30", "2025-01-02", CaptureDatePrecision.Exact, "30. Dezember 2024 – 2. Januar 2025")]
    [InlineData("2022-10-21", "2022-10-21", CaptureDatePrecision.Day, "Freitag, 21. Oktober 2022")]
    [InlineData("2025-03-01", "2025-03-02", CaptureDatePrecision.Exact, "1.–2. März 2025")]
    [InlineData("2025-07-15", "2025-07-15", CaptureDatePrecision.Month, "Juli 2025")]
    [InlineData("2025-07-01", "2025-07-01", CaptureDatePrecision.Year, "2025")]
    [InlineData("1987-07-01", "1989-07-01", CaptureDatePrecision.Year, "1987–1989")]
    [InlineData("2025-07-19", "2025-07-12", CaptureDatePrecision.Exact, "12.–19. Juli 2025")]
    public void Zeitraeume(string first, string last, CaptureDatePrecision precision, string expected)
        => StoryTitleBuilder.Range(DateOnly.Parse(first), DateOnly.Parse(last), precision).Should().Be(expected);

    [Theory]
    [InlineData(new[] { "Grado" }, null, "Grado")]
    [InlineData(new[] { "Grado", "Triest" }, null, "Grado · Triest")]
    [InlineData(new[] { "Grado", "Triest", "Aquileia" }, "Italien", "Grado · Triest · Aquileia — Italien")]
    [InlineData(new[] { "Grado", "Triest", "Aquileia", "Udine" }, null, "Grado, Triest und 2 weitere")]
    [InlineData(new[] { "Grado", "Triest", "Aquileia", "Udine", "Lignano" }, "Italien", "Grado, Triest und 3 weitere — Italien")]
    [InlineData(new[] { "Wien", "wien" }, null, "Wien")]
    public void Orte(string[] names, string? country, string expected)
        => StoryTitleBuilder.Places(names, country).Should().Be(expected);

    [Fact]
    public void Ohne_Orte_keine_Zeile()
        => StoryTitleBuilder.Places([]).Should().BeNull();

    [Fact]
    public void Personen_Orte_Rueckblicke_und_Zaehler()
    {
        StoryTitleBuilder.Person("Anna", 2019, 2019).Should().Be("Anna · 2019");
        StoryTitleBuilder.Person("Max", 2021, 2023).Should().Be("Max · 2021–2023");
        StoryTitleBuilder.Place("Grado", 2014, 2025).Should().Be("Grado · 2014–2025");
        StoryTitleBuilder.YearsAgo(3).Should().Be("Heute vor 3 Jahren");
        StoryTitleBuilder.YearsAgo(1).Should().Be("Heute vor einem Jahr");
        StoryTitleBuilder.MonthInEarlierYears(10).Should().Be("Oktober in früheren Jahren");
        StoryTitleBuilder.MonthYears(10, 2015, 2024).Should().Be("Oktober · 2015–2024");
        StoryTitleBuilder.Counts(430, 9).Should().Be("421 Fotos · 9 Videos");
        StoryTitleBuilder.Counts(1, 0).Should().Be("1 Foto");
        StoryTitleBuilder.Counts(1340, 0).Should().Be("1.340 Fotos");
        StoryTitleBuilder.Counts(2, 2).Should().Be("2 Videos");
        StoryTitleBuilder.KindLabel(StoryKind.Event, isAway: true).Should().Be("Ausflug");
        StoryTitleBuilder.KindLabel(StoryKind.Trip, isAway: true).Should().Be("Reise");
        StoryTitleBuilder.ShortDay(new DateOnly(2025, 7, 12)).Should().Be("Sa 12.");
        StoryTitleBuilder.Day(new DateOnly(2025, 7, 13), withYear: false).Should().Be("Sonntag, 13. Juli");
    }
}
