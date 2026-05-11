using System;
using System.Threading.Tasks;

namespace PhotoViewerV3.PerfTests;

internal static class Program
{
    private const string SpikeSqliteBulk = "spike01-sqlite-bulk";
    private const string SpikeWicBenchmark = "spike04-wic-benchmark";

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
    }
}
