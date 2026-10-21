using System;
using System.Threading.Tasks;

namespace PhotoViewerV3.PerfTests;

internal static class Program
{
    private const string SpikeSqliteBulk = "spike01-sqlite-bulk";
    private const string SpikeWicBenchmark = "spike04-wic-benchmark";
    private const string SpikeFfmpegCancel = "spike05-ffmpeg-cancel";
    private const string SpikeStorageDetection = "spike06-storage-detection";
    private const string SpikeV01Scan = "spike-v01-scan";
    private const string SpikeThumbSize = "spike11-thumb-size";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0] switch
        {
            SpikeSqliteBulk => await Spikes.Spike01SqliteBulk.RunAsync(args[1..]),
            SpikeWicBenchmark => await Spikes.Wic.Spike04WicBenchmark.RunAsync(args[1..]),
            SpikeFfmpegCancel => await Spikes.Ffmpeg.Spike05FfmpegBenchmark.RunAsync(args[1..]),
            SpikeStorageDetection => await Spikes.Storage.Spike06StorageDetection.RunAsync(args[1..]),
            SpikeV01Scan => await Spikes.V01Scan.SpikeV01Scan.RunAsync(args[1..]),
            SpikeThumbSize => await Spikes.Wic.Spike11ThumbnailSize.RunAsync(args[1..]),
            _ => UnknownCommand(args[0]),
        };
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unbekannter Spike: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("PhotoViewerV3.PerfTests");
        Console.WriteLine();
        Console.WriteLine("Verfügbare Spikes:");
        Console.WriteLine($"  {SpikeSqliteBulk} [--rows N] [--folders N] [--db PATH]");
        Console.WriteLine("    SQLite-Bulk + FTS5 mit synthetischen FileRows.");
        Console.WriteLine($"  {SpikeWicBenchmark} [--count N] [--thumb-width PX] [--workers N] [--dir PATH]");
        Console.WriteLine("    WIC Decode-Benchmark + File-Lock-Stress + Atomic-Write-Test.");
        Console.WriteLine($"  {SpikeFfmpegCancel} [--input PATH]");
        Console.WriteLine("    FFmpeg Cancel/Timeout-Verhalten (benötigt ffmpeg.exe im PATH).");
        Console.WriteLine($"  {SpikeStorageDetection} [--path PATH ...]");
        Console.WriteLine("    Storage-Profile-Detection für mehrere Pfade (lokal / UNC).");
        Console.WriteLine($"  {SpikeV01Scan} --root PATH [--name NAME] [--clear]");
        Console.WriteLine("    V0.1 End-to-End-Scan: Migration → Library → Scan → DB-Schreibe → FTS.");
        Console.WriteLine($"  {SpikeThumbSize} --dir PATH --out PATH [--count 200]");
        Console.WriteLine("    Vorschaubild-Größe alt (lange Kante 320) gegen kurze Kante 480/400 (ADR-0043).");
    }
}
