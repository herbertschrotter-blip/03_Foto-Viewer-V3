using System.Diagnostics;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Duplicates;
using Xunit.Abstractions;

namespace PhotoViewerV3.Tests.Duplicates;

/// <summary>
/// Gruppen ähnlicher Bilder (ADR-0053): Bucket-Suche findet genau die Paare des Vollvergleichs, Stern-Gruppen ohne Ketten,
/// keine Datei in zwei Gruppen; Behalten-Regel in der dokumentierten Reihenfolge mit Begründung.
/// </summary>
public sealed class SimilarGroupingTests(ITestOutputHelper output)
{
    /// <summary>Zufällige Fingerabdrücke, ein Teil davon in kleinen Gruppen mit wenigen gekippten Bits.</summary>
    internal static List<ulong> Clustered(int count, int seed, double clusteredShare = 0.1, int maxFlips = 6)
    {
        var random = new Random(seed);
        var list = new List<ulong>(count);
        while (list.Count < count)
        {
            var center = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 32);
            list.Add(center);
            if (random.NextDouble() < clusteredShare)
            {
                var members = random.Next(1, 6);
                for (var m = 0; m < members && list.Count < count; m++)
                {
                    var h = center;
                    var flips = random.Next(0, maxFlips + 1);
                    for (var f = 0; f < flips; f++)
                    {
                        h ^= 1UL << random.Next(64);
                    }

                    list.Add(h);
                }
            }
        }

        return list;
    }

    private static HashSet<(int, int)> BruteForce(IReadOnlyList<ulong> hashes, int r)
    {
        var set = new HashSet<(int, int)>();
        for (var i = 0; i < hashes.Count; i++)
        {
            for (var j = i + 1; j < hashes.Count; j++)
            {
                if (PerceptualHash.Distance(hashes[i], hashes[j]) <= r)
                {
                    set.Add((i, j));
                }
            }
        }

        return set;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void Bucket_Suche_findet_genau_die_Paare_des_Vollvergleichs(int r)
    {
        var hashes = Clustered(3000, seed: r + 1, clusteredShare: 0.3, maxFlips: 14);
        SimilarGrouping.FindPairs(hashes, r).ToHashSet().Should().BeEquivalentTo(BruteForce(hashes, r));
    }

    [Fact]
    public void Stern_Gruppen_ohne_Ketten_und_ohne_Doppelbelegung()
    {
        // Kette 0 ~ 1 ~ 2 (je 4 Bit), 0 und 2 aber 8 Bit auseinander: bei r = 5 gehören nur 0 und 1 zusammen
        var a = 0UL;
        var b = 0b1111UL;
        var c = 0b1111_1111UL;
        var hashes = new List<ulong> { a, b, c, 0xFFFF_0000_0000_0000UL };
        var groups = SimilarGrouping.Group(hashes, 5);

        groups.Should().ContainSingle();
        groups[0].Should().Equal(0, 1);

        var many = Clustered(5000, seed: 3, clusteredShare: 0.3);
        var all = SimilarGrouping.Group(many, 8);
        all.SelectMany(g => g).Should().OnlyHaveUniqueItems("keine Datei in zwei Gruppen");
        foreach (var group in all)
        {
            group.Length.Should().BeGreaterThan(1);
            group.Skip(1).Should().OnlyContain(i => PerceptualHash.Distance(many[group[0]], many[i]) <= 8, "jedes Mitglied nah am Mittelpunkt");
        }
    }

    [Fact]
    public void Abstand_ausserhalb_des_Bereichs_wird_abgelehnt()
    {
        var act = () => SimilarGrouping.Group([1UL, 2UL], 16);
        act.Should().Throw<ArgumentOutOfRangeException>();
        SimilarGrouping.Group([1UL], 10).Should().BeEmpty();
    }

    [Fact]
    public void Messung_100k_Fingerabdruecke()
    {
        var hashes = Clustered(100_000, seed: 9);
        var sw = Stopwatch.StartNew();
        var groups = SimilarGrouping.Group(hashes, DuplicateSettings.DefaultSimilarThreshold);
        sw.Stop();
        output.WriteLine($"100 000 Fingerabdrücke, r = {DuplicateSettings.DefaultSimilarThreshold}: {groups.Count} Gruppen in {sw.ElapsedMilliseconds} ms");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        groups.Count.Should().BeGreaterThan(5000);
    }

    private static DuplicateMember Member(
        int id,
        int? width = 4000,
        int? height = 3000,
        CaptureDateSource? source = CaptureDateSource.Exif,
        bool inbox = false,
        byte? rating = null,
        bool favorite = false,
        int tags = 0,
        long size = 1000)
        => new(id, 1, "B", $"f{id}.jpg", @"C:\x", MediaType.Image, size, width, height,
            source is null ? null : new DateTime(2024, 7, 12, 0, 0, 0, DateTimeKind.Utc), source, null, rating, favorite, tags, 0, 0, inbox,
            DateTime.UtcNow, null);

    [Fact]
    public void Behalten_Regel_in_der_dokumentierten_Reihenfolge()
    {
        // 1. Auflösung schlägt alles andere
        var order = KeeperRule.Order([Member(1, 1600, 1200, inbox: false, rating: 5, favorite: true), Member(2, inbox: true)]);
        order[0].FileId.Should().Be(2);
        KeeperRule.Reason(order[0], order[1]).Should().Be("höhere Auflösung (4000 × 3000 statt 1600 × 1200)");

        // 2. Aufnahmedatum aus der Datei vor Bibliothek/Eingang
        order = KeeperRule.Order([Member(1, source: CaptureDateSource.FileName), Member(2, inbox: true)]);
        order[0].FileId.Should().Be(2);
        KeeperRule.Reason(order[0], order[1]).Should().Be("Aufnahmedatum aus der Datei vorhanden");

        // 3. Bibliothek vor Eingang
        order = KeeperRule.Order([Member(1, inbox: true, rating: 5), Member(2)]);
        order[0].FileId.Should().Be(2);
        KeeperRule.Reason(order[0], order[1]).Should().Be("liegt schon in der Bibliothek");

        // 4. eigene Angaben
        order = KeeperRule.Order([Member(1), Member(2, tags: 2)]);
        order[0].FileId.Should().Be(2);
        KeeperRule.Reason(order[0], order[1]).Should().StartWith("mehr eigene Angaben");

        // 5. größere Datei, 6. kleinere FileId
        KeeperRule.Order([Member(1, size: 10), Member(2, size: 20)])[0].FileId.Should().Be(2);
        order = KeeperRule.Order([Member(7), Member(3)]);
        order[0].FileId.Should().Be(3);
        KeeperRule.Reason(order[0], order[1]).Should().Be("gleichwertig — die länger im Katalog");
        KeeperRule.Resolution(Member(1, null, null)).Should().Be("unbekannt");
    }
}
