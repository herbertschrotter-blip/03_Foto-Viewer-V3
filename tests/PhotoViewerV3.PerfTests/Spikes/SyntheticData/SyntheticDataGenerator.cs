using System;
using System.Collections.Generic;
using System.Globalization;

namespace PhotoViewerV3.PerfTests.Spikes.SyntheticData;

/// <summary>
/// Erzeugt reproduzierbare synthetische Folder- und FileRows für Spike-Messungen.
/// Folders eager als Liste, Files als Stream — Bulk-Insert konsumiert ohne 500k in RAM zu halten.
/// </summary>
internal static class SyntheticDataGenerator
{
    private const int LibraryId = 1;
    private const string LibraryRoot = @"E:\Fotos";

    // 80 % Images / 20 % Videos
    private const double VideoShare = 0.20;

    // 80 % der Files liegen in 20 % der Folders (Pareto-Approximation)
    private const double HeavyFolderShare = 0.20;
    private const double HeavyFolderFileShare = 0.80;

    private const double FavoriteShare = 0.05;

    private static readonly DateTime EpochStart = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan SpreadOverYears = TimeSpan.FromDays(5 * 365.25);

    private static readonly string[] TopicVocabulary =
    [
        "Urlaub", "Familie", "Geburtstag", "Sonnenuntergang", "Strand", "Berge", "Stadt",
        "Tier", "Wandern", "Hochzeit", "Weihnachten", "Ostern", "Reise", "Italien", "Spanien",
        "Norwegen", "Muenchen", "Berlin", "Wien", "Paris", "Garten", "Konzert", "Festival",
        "Schnee", "Sommer", "Herbst", "Winter", "Fruehling", "Wald", "See", "Fluss", "Markt",
        "Cafe", "Hund", "Katze", "Pferd", "Auto", "Fahrrad", "Boot", "Flugzeug", "Zug",
        "Brunch", "Abendessen", "Picknick", "Sport", "Lauf", "Yoga", "Ski", "Schwimmen",
    ];

    private static readonly string[] FolderTopics =
    [
        "Urlaub", "Familie", "Events", "Reisen", "Natur", "Tiere", "Sport", "Feste",
        "Hochzeit", "Geburtstag", "Weihnachten", "Ostern", "Sommer", "Winter",
        "Wochenende", "Ausfluege", "Berge", "See", "Strand", "Stadt",
    ];

    private static readonly string[] FolderYearSuffixes =
    [
        "2021", "2022", "2023", "2024", "2025", "2026",
    ];

    public static IReadOnlyList<SyntheticFolder> GenerateFolders(int count, int seed)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var random = new Random(seed);
        var folders = new List<SyntheticFolder>(count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var topLevelCount = Math.Max(1, count / 200);

        for (var i = 0; i < topLevelCount; i++)
        {
            var (name, path) = GenerateUniqueTopLevelPath(random, paths);
            folders.Add(new SyntheticFolder(
                Id: i + 1,
                ParentId: null,
                Path: path,
                Name: name));
        }

        for (var i = topLevelCount; i < count; i++)
        {
            var parent = folders[random.Next(0, Math.Min(folders.Count, topLevelCount * 4))];
            var (name, path) = GenerateUniqueSubFolderPath(random, parent, paths, i);
            folders.Add(new SyntheticFolder(
                Id: i + 1,
                ParentId: parent.Id,
                Path: path,
                Name: name));
        }

        return folders;
    }

    private static (string Name, string Path) GenerateUniqueTopLevelPath(Random random, HashSet<string> taken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var name = $"{PickTopic(random)}_{PickYear(random)}";
            var path = $"{LibraryRoot}\\{name}";
            if (taken.Add(path))
            {
                return (name, path);
            }
        }

        while (true)
        {
            var seq = random.Next(1, 99_999).ToString("D5", CultureInfo.InvariantCulture);
            var name = $"{PickTopic(random)}_{PickYear(random)}_{seq}";
            var path = $"{LibraryRoot}\\{name}";
            if (taken.Add(path))
            {
                return (name, path);
            }
        }
    }

    private static (string Name, string Path) GenerateUniqueSubFolderPath(
        Random random,
        SyntheticFolder parent,
        HashSet<string> taken,
        int idx)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var name = BuildSubFolderName(random, idx);
            var path = $"{parent.Path}\\{name}";
            if (taken.Add(path))
            {
                return (name, path);
            }
        }

        while (true)
        {
            var name = $"{PickFolderTopic(random)}_{idx + 1:D7}_{random.Next(1, 99_999):D5}";
            var path = $"{parent.Path}\\{name}";
            if (taken.Add(path))
            {
                return (name, path);
            }
        }
    }

    public static IEnumerable<SyntheticFile> GenerateFiles(
        IReadOnlyList<SyntheticFolder> folders,
        int totalCount,
        int seed)
    {
        if (folders.Count == 0)
        {
            throw new ArgumentException("Mindestens ein Folder erforderlich.", nameof(folders));
        }

        if (totalCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCount));
        }

        var random = new Random(seed);
        var folderWeights = BuildFolderWeights(folders.Count, random);

        var nextId = 1;
        for (var i = 0; i < totalCount; i++)
        {
            var folder = folders[PickWeightedIndex(folderWeights, random)];
            yield return CreateFile(nextId++, folder, random);
        }
    }

    private static SyntheticFile CreateFile(int id, SyntheticFolder folder, Random random)
    {
        var isVideo = random.NextDouble() < VideoShare;
        var modifiedAt = EpochStart + TimeSpan.FromSeconds(random.NextDouble() * SpreadOverYears.TotalSeconds);
        var createdAt = modifiedAt - TimeSpan.FromSeconds(random.Next(0, 60 * 60 * 24));
        var isFavorite = random.NextDouble() < FavoriteShare;

        if (isVideo)
        {
            var (name, ext) = BuildVideoName(random, modifiedAt);
            var size = (long)random.Next(10_000_000, 500_000_000); // 10-500 MB
            return new SyntheticFile(
                Id: id,
                FolderId: folder.Id,
                Name: name,
                Extension: ext,
                Size: size,
                CreatedAtUtc: createdAt,
                ModifiedAtUtc: modifiedAt,
                MediaType: SyntheticMediaType.Video,
                Width: PickResolution(random).Width,
                Height: PickResolution(random).Height,
                DurationMs: random.Next(5_000, 30 * 60_000),
                Codec: random.NextDouble() < 0.7 ? "h264" : "hevc",
                IsFavorite: isFavorite,
                Rating: null,
                Missing: false);
        }
        else
        {
            var (name, ext) = BuildImageName(random, modifiedAt);
            var size = (long)random.Next(500_000, 8_000_000); // 0.5-8 MB
            var (w, h) = PickResolution(random);
            return new SyntheticFile(
                Id: id,
                FolderId: folder.Id,
                Name: name,
                Extension: ext,
                Size: size,
                CreatedAtUtc: createdAt,
                ModifiedAtUtc: modifiedAt,
                MediaType: SyntheticMediaType.Image,
                Width: w,
                Height: h,
                DurationMs: null,
                Codec: null,
                IsFavorite: isFavorite,
                Rating: null,
                Missing: false);
        }
    }

    private static (string Name, string Extension) BuildImageName(Random random, DateTime stamp)
    {
        var roll = random.NextDouble();
        if (roll < 0.50)
        {
            var name = $"IMG_{stamp:yyyyMMdd_HHmmss}_{random.Next(0, 9999):D4}.jpg";
            return (name, ".jpg");
        }

        if (roll < 0.70)
        {
            var name = $"DSC_{random.Next(0, 99999):D5}.jpg";
            return (name, ".jpg");
        }

        if (roll < 0.80)
        {
            var name = $"screenshot_{stamp:yyyyMMdd-HHmmss}.png";
            return (name, ".png");
        }

        var topic = PickTopic(random);
        var topic2 = PickTopic(random);
        var seq = random.Next(1, 999).ToString("D3", CultureInfo.InvariantCulture);
        var ext = random.NextDouble() < 0.9 ? ".jpg" : ".png";
        return ($"{topic}_{topic2}_{seq}{ext}", ext);
    }

    private static (string Name, string Extension) BuildVideoName(Random random, DateTime stamp)
    {
        var roll = random.NextDouble();
        if (roll < 0.40)
        {
            return ($"VID_{stamp:yyyyMMdd_HHmmss}.mp4", ".mp4");
        }

        if (roll < 0.70)
        {
            var topic = PickTopic(random);
            var seq = random.Next(1, 999).ToString("D3", CultureInfo.InvariantCulture);
            return ($"Urlaub_{topic}_{seq}.mp4", ".mp4");
        }

        return ($"Aufnahme_{stamp:yyyy-MM-dd}_{random.Next(1, 99):D2}.mp4", ".mp4");
    }

    private static string BuildSubFolderName(Random random, int idx)
    {
        var roll = random.NextDouble();
        if (roll < 0.40)
        {
            return $"{PickTopic(random)}_{PickYear(random)}";
        }

        if (roll < 0.70)
        {
            return $"{PickFolderTopic(random)}_{random.Next(1, 31):D2}";
        }

        return $"{PickFolderTopic(random)}_{idx + 1:D5}";
    }

    private static string PickTopic(Random random) =>
        TopicVocabulary[random.Next(TopicVocabulary.Length)];

    private static string PickFolderTopic(Random random) =>
        FolderTopics[random.Next(FolderTopics.Length)];

    private static string PickYear(Random random) =>
        FolderYearSuffixes[random.Next(FolderYearSuffixes.Length)];

    private static (int Width, int Height) PickResolution(Random random)
    {
        return random.Next(0, 5) switch
        {
            0 => (4032, 3024),
            1 => (3840, 2160),
            2 => (1920, 1080),
            3 => (6000, 4000),
            _ => (4000, 3000),
        };
    }

    /// <summary>
    /// Pareto-Gewichte: 20 % der Folders sammeln 80 % der Files.
    /// Innerhalb jeder Gruppe Variation ±50 %, deterministisch via Seed.
    /// </summary>
    private static double[] BuildFolderWeights(int folderCount, Random random)
    {
        var weights = new double[folderCount];
        var heavyCount = Math.Max(1, (int)(folderCount * HeavyFolderShare));

        var heavyBudget = HeavyFolderFileShare / heavyCount;
        var lightBudget = (1.0 - HeavyFolderFileShare) / Math.Max(1, folderCount - heavyCount);

        for (var i = 0; i < heavyCount; i++)
        {
            weights[i] = heavyBudget * (0.5 + random.NextDouble());
        }

        for (var i = heavyCount; i < folderCount; i++)
        {
            weights[i] = lightBudget * (0.5 + random.NextDouble());
        }

        // Fisher-Yates: durchmischen, damit Heavy-Folders nicht alle am Anfang stehen
        for (var i = weights.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (weights[i], weights[j]) = (weights[j], weights[i]);
        }

        // Kumulieren für O(log N)-Pick mit BinarySearch
        var sum = 0.0;
        for (var i = 0; i < weights.Length; i++)
        {
            sum += weights[i];
            weights[i] = sum;
        }

        // Normalisieren auf [0, 1)
        for (var i = 0; i < weights.Length; i++)
        {
            weights[i] /= sum;
        }

        return weights;
    }

    private static int PickWeightedIndex(double[] cumulativeWeights, Random random)
    {
        var r = random.NextDouble();
        var idx = Array.BinarySearch(cumulativeWeights, r);
        return idx >= 0 ? idx : ~idx;
    }
}
