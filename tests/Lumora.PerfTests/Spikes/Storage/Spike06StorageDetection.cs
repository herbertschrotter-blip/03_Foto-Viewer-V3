using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Lumora.PerfTests.Spikes.Storage;

internal static class Spike06StorageDetection
{
    public static async Task<int> RunAsync(string[] args)
    {
        var customPaths = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--path" && i + 1 < args.Length)
            {
                customPaths.Add(args[++i]);
            }
        }

        Console.WriteLine("Spike #06 — Storage-Detection");
        Console.WriteLine();

        var testPaths = new List<string>();
        if (customPaths.Count > 0)
        {
            testPaths.AddRange(customPaths);
        }
        else
        {
            // Default-Liste: System-Drive, Working-Dir-Drive, OneDrive (falls da), offline-UNC
            testPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
            testPaths.Add(Directory.GetCurrentDirectory());
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrEmpty(oneDrive))
            {
                testPaths.Add(oneDrive);
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                testPaths.Add(userProfile);
            }

            testPaths.Add(@"\\nonexistent-spike-host\share");
        }

        Console.WriteLine($"  Test-Pfade: {testPaths.Count}");
        Console.WriteLine();
        Console.WriteLine($"    {"Path",-50} {"Profile",-18} {"Erreichbar",-12} {"Dauer",-9} Grund");
        Console.WriteLine($"    {new string('-', 50)} {new string('-', 18)} {new string('-', 12)} {new string('-', 9)} -----");

        var maxDuration = TimeSpan.Zero;
        var uncMax = TimeSpan.Zero;
        var localMax = TimeSpan.Zero;
        var rowCount = 0;

        foreach (var p in testPaths)
        {
            var result = await StorageProfileDetector.DetectAsync(p).ConfigureAwait(false);
            var trunc = Truncate(p, 50);
            var dur = $"{result.Duration.TotalMilliseconds,6:N0} ms";
            Console.WriteLine($"    {trunc,-50} {result.Profile,-18} {result.RootReachable,-12} {dur,-9} {result.Reason}");
            if (result.Duration > maxDuration) maxDuration = result.Duration;
            if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                if (result.Duration > uncMax) uncMax = result.Duration;
            }
            else
            {
                if (result.Duration > localMax) localMax = result.Duration;
            }
            rowCount++;
        }

        Console.WriteLine();
        Console.WriteLine("Ziele:");
        Console.WriteLine($"  Lokal worst-case  : {localMax.TotalMilliseconds,5:N0} ms   (Ziel < 100 ms)   → {(localMax.TotalMilliseconds < 100 ? "GRÜN" : localMax.TotalMilliseconds < 300 ? "GELB" : "ROT")}");
        Console.WriteLine($"  UNC worst-case    : {uncMax.TotalMilliseconds,5:N0} ms   (Ziel < 3 s)      → {(uncMax.TotalMilliseconds < 3000 ? "GRÜN" : "ROT")}");
        return 0;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
