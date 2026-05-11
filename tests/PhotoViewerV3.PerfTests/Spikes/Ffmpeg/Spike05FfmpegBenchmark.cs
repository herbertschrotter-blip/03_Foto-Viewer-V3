using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.PerfTests.Spikes.Ffmpeg;

internal static class Spike05FfmpegBenchmark
{
    private const string SpikeDataFolderName = "spike-data";
    private const string SintelFileName = "sintel_trailer-480p.mp4";
    private const string SpikeFolderName = "spike-05";

    public static async Task<int> RunAsync(string[] args)
    {
        var inputOverride = ParseInputOverride(args);

        Console.WriteLine("Spike #05 — FFmpeg Cancel/Timeout");
        Console.WriteLine();

        var ffmpegExe = FfmpegProcessRunner.TryLocateFfmpeg();
        if (ffmpegExe is null)
        {
            Console.Error.WriteLine("ffmpeg.exe nicht gefunden. Installation:");
            Console.Error.WriteLine("  winget install Gyan.FFmpeg");
            Console.Error.WriteLine("Danach PowerShell neu starten und Spike erneut ausführen.");
            return 2;
        }

        Console.WriteLine($"  FFmpeg:  {ffmpegExe}");

        var paths = new AppDataPaths();
        var spikeDir = Path.Combine(paths.AppDataRoot, SpikeFolderName);
        Directory.CreateDirectory(spikeDir);
        var sintelPath = inputOverride ?? Path.Combine(paths.AppDataRoot, SpikeDataFolderName, SintelFileName);

        if (!File.Exists(sintelPath))
        {
            Console.Error.WriteLine($"Sintel-MP4 nicht gefunden: {sintelPath}");
            Console.Error.WriteLine("Bitte vorher Spike #3 (Lightbox) ausführen, der lädt das Sample automatisch.");
            Console.Error.WriteLine("Oder per --input PATH ein eigenes MP4 angeben.");
            return 3;
        }

        Console.WriteLine($"  Healthy: {sintelPath} ({new FileInfo(sintelPath).Length / 1024.0 / 1024.0:N1} MB)");

        // Test 1: Healthy MP4 mit großzügigem Timeout
        var test1Path = Path.Combine(spikeDir, "frame-healthy.jpg");
        Console.WriteLine();
        Console.WriteLine("[1] Healthy MP4 mit 5 s Timeout — sollte erfolgreich sein");
        var r1 = await FfmpegProcessRunner.ExtractStillFrameAsync(ffmpegExe, sintelPath, test1Path, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        PrintResult(r1, test1Path);

        // Test 2: Slow Reencode mit 500 ms Timeout — soll canceln (langsamer Job, kurzer Timeout)
        var test2Path = Path.Combine(spikeDir, "reencode-timeout.mp4");
        Console.WriteLine();
        Console.WriteLine("[2] Slow Reencode (libx264 slower) mit 500 ms Timeout — sollte canceln (Process.Kill)");
        var r2 = await FfmpegProcessRunner.SlowReencodeAsync(ffmpegExe, sintelPath, test2Path, TimeSpan.FromMilliseconds(500), CancellationToken.None).ConfigureAwait(false);
        PrintResult(r2, test2Path);

        // Test 3: Truncated MP4 (50 KB) — vermutlich kaputt, soll mit 3 s Timeout canceln oder sauber fail
        var truncatedPath = Path.Combine(spikeDir, "truncated.mp4");
        CreateTruncatedCopy(sintelPath, truncatedPath, 50 * 1024);
        var test3Path = Path.Combine(spikeDir, "frame-truncated.jpg");
        Console.WriteLine();
        Console.WriteLine($"[3] Truncated MP4 ({new FileInfo(truncatedPath).Length / 1024.0:N0} KB) mit 3 s Timeout — soll fail/cancel, kein Hang");
        var r3 = await FfmpegProcessRunner.ExtractStillFrameAsync(ffmpegExe, truncatedPath, test3Path, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        PrintResult(r3, test3Path);

        // Test 4: Non-existent input
        var test4Path = Path.Combine(spikeDir, "frame-nonexistent.jpg");
        Console.WriteLine();
        Console.WriteLine("[4] Non-existent Input mit 3 s Timeout — sollte schnell fail");
        var r4 = await FfmpegProcessRunner.ExtractStillFrameAsync(ffmpegExe, Path.Combine(spikeDir, "does-not-exist.mp4"), test4Path, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        PrintResult(r4, test4Path);

        // Test 5: Slow Reencode mit großzügigem Timeout, aber External Cancel nach 300 ms
        var test5Path = Path.Combine(spikeDir, "reencode-extCancel.mp4");
        Console.WriteLine();
        Console.WriteLine("[5] Slow Reencode + External CancellationToken Cancel nach 300 ms — soll < 800 ms exiten");
        using var extCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var r5 = await FfmpegProcessRunner.SlowReencodeAsync(ffmpegExe, sintelPath, test5Path, TimeSpan.FromSeconds(30), extCts.Token).ConfigureAwait(false);
        PrintResult(r5, test5Path);

        // Summary
        Console.WriteLine();
        Console.WriteLine("=== Spike #05 Summary ===");
        Console.WriteLine("  [1] Healthy frame:     erwartet success=true, cancel=false           →  " + Status(r1.Success && !r1.Cancelled));
        Console.WriteLine("  [2] Slow reencode TO:  erwartet cancel=true innerhalb ~800 ms        →  " + Status(r2.Cancelled && r2.Duration < TimeSpan.FromMilliseconds(1500)));
        Console.WriteLine("  [3] Truncated:         erwartet kein Hang, sauber fail < 3.5 s       →  " + Status(r3.Duration < TimeSpan.FromSeconds(3.5)));
        Console.WriteLine("  [4] Non-existent:      erwartet success=false, fast fail < 2 s       →  " + Status(!r4.Success && r4.Duration < TimeSpan.FromSeconds(2)));
        Console.WriteLine("  [5] External Cancel:   erwartet cancel=true innerhalb ~600 ms        →  " + Status(r5.Cancelled && r5.Duration < TimeSpan.FromMilliseconds(800)));

        return 0;
    }

    private static void PrintResult(FfmpegResult r, string outputPath)
    {
        var outExists = File.Exists(outputPath);
        var outSize = outExists ? new FileInfo(outputPath).Length : 0;
        Console.WriteLine($"    Success: {r.Success}, Cancelled: {r.Cancelled}, ExitCode: {r.ExitCode}, Duration: {r.Duration.TotalMilliseconds,7:N0} ms");
        Console.WriteLine($"    Output:  {outputPath} {(outExists ? $"({outSize / 1024.0:N1} KB)" : "(nicht erzeugt)")}");
        Console.WriteLine($"    StdErr:  {r.StdErrTail}");
    }

    private static string Status(bool ok) => ok ? "🟢 GRÜN" : "🔴 ROT";

    private static void CreateTruncatedCopy(string source, string dest, int byteCount)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write);
        var buffer = new byte[byteCount];
        var read = src.Read(buffer, 0, byteCount);
        dst.Write(buffer, 0, read);
    }

    private static string? ParseInputOverride(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--input" && i + 1 < args.Length)
            {
                return args[++i];
            }
        }

        return null;
    }
}
