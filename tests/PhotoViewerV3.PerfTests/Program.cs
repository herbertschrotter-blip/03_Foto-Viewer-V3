using System;
using System.Threading.Tasks;

namespace PhotoViewerV3.PerfTests;

internal static class Program
{
    private const string SpikeSqliteBulk = "spike01-sqlite-bulk";

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
    }
}
