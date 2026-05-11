using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.PerfTests.Spikes.Wic;

internal sealed record BenchmarkReport(
    string Name,
    int Count,
    double TotalSeconds,
    double Throughput,
    double MedianMs,
    double P95Ms,
    double MinMs,
    double MaxMs,
    int Errors)
{
    public string AmpelFor(double medianTargetMs, double minThroughput)
    {
        if (Errors > 0) return "🔴";
        if (MedianMs > medianTargetMs || Throughput < minThroughput) return "🟡";
        return "🟢";
    }
}

internal static class Spike04WicBenchmark
{
    private const int DefaultCount = 1000;
    private const int DefaultThumbWidth = 320;
    private const int GeneratorSeed = 20260511;
    private const string SpikeFolderName = "spike-04";
    private const string SynthSubfolder = "synth-jpgs";
    private const string ThumbsSubfolder = "thumbs";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = ParseArgs(args);
        Console.WriteLine("Spike #04 — WIC Benchmark + File-Lock-Test");
        Console.WriteLine($"  Count:        {options.Count:N0}");
        Console.WriteLine($"  Thumb-Width:  {options.ThumbWidth} px");
        Console.WriteLine($"  Workers:      {options.ParallelWorkers}");
        Console.WriteLine($"  Spike-Dir:    {options.SpikeDir}");
        Console.WriteLine();

        // [1] Synthetische JPGs vorbereiten (oder reuse, falls schon da)
        var synthDir = Path.Combine(options.SpikeDir, SynthSubfolder);
        var report = await PrepareSampleSetAsync(options.Count, synthDir).ConfigureAwait(false);
        Console.WriteLine();

        // [2] Decode-Benchmarks
        var results = new List<BenchmarkReport>();
        Console.WriteLine("[2] Decode-Benchmarks (3 Warmup verworfen, 5 Iterationen gemessen)…");
        results.Add(RunBenchmark(
            name: "Single-thread, OnLoad+FileShare",
            files: report.Files,
            workers: 1,
            decoder: f => WicDecoder.DecodeViaSharedStream(f.Path, options.ThumbWidth)));
        results.Add(RunBenchmark(
            name: $"Parallel {Math.Max(2, Math.Min(4, options.ParallelWorkers))} Workers",
            files: report.Files,
            workers: Math.Max(2, Math.Min(4, options.ParallelWorkers)),
            decoder: f => WicDecoder.DecodeViaSharedStream(f.Path, options.ThumbWidth)));
        results.Add(RunBenchmark(
            name: $"Parallel {options.ParallelWorkers} Workers (max)",
            files: report.Files,
            workers: options.ParallelWorkers,
            decoder: f => WicDecoder.DecodeViaSharedStream(f.Path, options.ThumbWidth)));
        results.Add(RunBenchmark(
            name: "UriSource-Variante (Vergleich)",
            files: report.Files,
            workers: 4,
            decoder: f => WicDecoder.DecodeViaUri(f.Path, options.ThumbWidth)));

        // [3] File-Lock-Stress-Test
        Console.WriteLine();
        Console.WriteLine("[3] File-Lock-Stress-Test (Decoder läuft, parallel Stress-Writer rewriten dieselben Files)…");
        var lockStress = await RunFileLockStressAsync(report.Files, options.ThumbWidth).ConfigureAwait(false);
        results.Add(lockStress);

        // [4] Atomic-Write-Test
        Console.WriteLine();
        Console.WriteLine("[4] Atomic-Write-Test (Decode + .tmp + Move)…");
        var thumbsDir = Path.Combine(options.SpikeDir, ThumbsSubfolder);
        results.Add(RunAtomicWriteBenchmark(report.Files, thumbsDir, options.ThumbWidth));

        // [5] Summary
        Console.WriteLine();
        PrintSummary(results, options);

        return 0;
    }

    private static async Task<SynthGenReport> PrepareSampleSetAsync(int count, string synthDir)
    {
        Directory.CreateDirectory(synthDir);
        var existing = Directory.GetFiles(synthDir, "synth_*.jpg");
        if (existing.Length >= count)
        {
            // Reuse — collect first N
            var files = existing
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .Select(p => ParseSynthFile(p))
                .ToList();
            var total = files.Sum(f => f.SizeBytes);
            Console.WriteLine($"[1] {count:N0} synth. JPGs bereits vorhanden ({total / 1024.0 / 1024.0:N1} MB).");
            return new SynthGenReport(count, total, TimeSpan.Zero, files);
        }

        Console.WriteLine($"[1] Generiere {count:N0} synth. JPGs (gradient + noise)…");
        var report = await Task.Run(() => SyntheticJpgGenerator.Generate(count, synthDir, GeneratorSeed)).ConfigureAwait(false);
        Console.WriteLine($"    Fertig: {report.Count:N0} Files, {report.TotalBytes / 1024.0 / 1024.0:N1} MB in {report.Duration.TotalSeconds:N1} s ({report.MBytesPerSec:N1} MB/s Encode)");
        return report;
    }

    private static SynthJpgFile ParseSynthFile(string path)
    {
        // synth_00042_1920x1080.jpg
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('_');
        int w = 0, h = 0;
        if (parts.Length >= 3 && parts[2].Contains('x'))
        {
            var dims = parts[2].Split('x');
            int.TryParse(dims[0], out w);
            int.TryParse(dims[1], out h);
        }

        return new SynthJpgFile(path, w, h, new FileInfo(path).Length);
    }

    private static BenchmarkReport RunBenchmark(
        string name,
        IReadOnlyList<SynthJpgFile> files,
        int workers,
        Func<SynthJpgFile, DecodeResult> decoder)
    {
        // Warmup
        for (var w = 0; w < 3; w++)
        {
            DecodeAll(files, workers, decoder, out _);
        }

        var iterations = new double[5];
        var errorsSum = 0;
        for (var i = 0; i < iterations.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            DecodeAll(files, workers, decoder, out var errors);
            sw.Stop();
            iterations[i] = sw.Elapsed.TotalMilliseconds;
            errorsSum += errors;
        }

        Array.Sort(iterations);
        var totalMs = iterations[iterations.Length / 2]; // Median run
        var totalSec = totalMs / 1000.0;
        var perItemMs = totalMs / files.Count;
        var throughput = files.Count / totalSec;

        // Individual latencies aus dem Median-Run für p95
        var latencies = MeasureIndividualLatencies(files, workers, decoder, out var errorsInd);
        Array.Sort(latencies);
        var p95 = latencies[(int)Math.Ceiling((latencies.Length - 1) * 0.95)];
        var min = latencies[0];
        var max = latencies[latencies.Length - 1];

        var report = new BenchmarkReport(name, files.Count, totalSec, throughput, perItemMs, p95, min, max, errorsSum + errorsInd);
        var statusFlag = (errorsSum + errorsInd) > 0 ? "ERRORS!" : "OK";
        Console.WriteLine($"    {name,-50} → {totalSec,5:N2} s   {throughput,8:N0}/s   med {perItemMs,6:N2} ms   p95 {p95,6:N2} ms   {statusFlag}");
        return report;
    }

    private static void DecodeAll(
        IReadOnlyList<SynthJpgFile> files,
        int workers,
        Func<SynthJpgFile, DecodeResult> decoder,
        out int errors)
    {
        var errCount = 0;
        if (workers == 1)
        {
            foreach (var f in files)
            {
                try
                {
                    _ = decoder(f);
                }
                catch
                {
                    Interlocked.Increment(ref errCount);
                }
            }
        }
        else
        {
            Parallel.ForEach(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = workers },
                f =>
                {
                    try
                    {
                        _ = decoder(f);
                    }
                    catch
                    {
                        Interlocked.Increment(ref errCount);
                    }
                });
        }

        errors = errCount;
    }

    private static double[] MeasureIndividualLatencies(
        IReadOnlyList<SynthJpgFile> files,
        int workers,
        Func<SynthJpgFile, DecodeResult> decoder,
        out int errors)
    {
        var latencies = new double[files.Count];
        var errCount = 0;
        if (workers == 1)
        {
            for (var i = 0; i < files.Count; i++)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    _ = decoder(files[i]);
                }
                catch
                {
                    Interlocked.Increment(ref errCount);
                }

                sw.Stop();
                latencies[i] = sw.Elapsed.TotalMilliseconds;
            }
        }
        else
        {
            Parallel.For(
                0,
                files.Count,
                new ParallelOptions { MaxDegreeOfParallelism = workers },
                i =>
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        _ = decoder(files[i]);
                    }
                    catch
                    {
                        Interlocked.Increment(ref errCount);
                    }

                    sw.Stop();
                    latencies[i] = sw.Elapsed.TotalMilliseconds;
                });
        }

        errors = errCount;
        return latencies;
    }

    private static async Task<BenchmarkReport> RunFileLockStressAsync(
        IReadOnlyList<SynthJpgFile> files,
        int thumbWidth)
    {
        // Stress-Writer rewriten zufällige Files mit FileShare.None alle ~50 ms.
        // Wenn Decoder File-Share-Pattern korrekt nutzt, gibt's keine Konflikte.
        using var stop = new CancellationTokenSource();
        var stressRandom = new Random(20260512);
        var stressErrors = 0;
        var stressTask = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var idx = stressRandom.Next(files.Count);
                    var f = files[idx];
                    var bytes = File.ReadAllBytes(f.Path);
                    var tempPath = f.Path + ".stress.tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(tempPath, bytes, stop.Token);
                        File.Move(tempPath, f.Path, overwrite: true);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref stressErrors);
                    }

                    await Task.Delay(20, stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var sw = Stopwatch.StartNew();
        DecodeAll(files, workers: 4, decoder: f => WicDecoder.DecodeViaSharedStream(f.Path, thumbWidth), out var decodeErrors);
        sw.Stop();
        stop.Cancel();
        await stressTask.ConfigureAwait(false);

        var totalSec = sw.Elapsed.TotalSeconds;
        var throughput = files.Count / totalSec;
        var perItem = sw.Elapsed.TotalMilliseconds / files.Count;
        var report = new BenchmarkReport(
            "Decode unter Stress-Writer (FileShare-Test)",
            files.Count,
            totalSec,
            throughput,
            perItem,
            perItem,
            perItem,
            perItem,
            decodeErrors);

        Console.WriteLine($"    Decoder-Errors: {decodeErrors}, Stress-Writer-Errors: {stressErrors}, {throughput:N0} thumbs/s");
        return report;
    }

    private static BenchmarkReport RunAtomicWriteBenchmark(
        IReadOnlyList<SynthJpgFile> files,
        string thumbsDir,
        int thumbWidth)
    {
        Directory.CreateDirectory(thumbsDir);
        var latencies = new double[files.Count];
        var errCount = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(
            0,
            files.Count,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            i =>
            {
                var src = files[i].Path;
                var thumbName = Path.GetFileNameWithoutExtension(src) + $"_t{thumbWidth}.jpg";
                var thumb = Path.Combine(thumbsDir, thumbName);
                var sw2 = Stopwatch.StartNew();
                try
                {
                    _ = WicDecoder.DecodeAndSaveAtomic(src, thumb, thumbWidth);
                }
                catch
                {
                    Interlocked.Increment(ref errCount);
                }

                sw2.Stop();
                latencies[i] = sw2.Elapsed.TotalMilliseconds;
            });
        sw.Stop();

        Array.Sort(latencies);
        var totalSec = sw.Elapsed.TotalSeconds;
        var throughput = files.Count / totalSec;
        var median = latencies[latencies.Length / 2];
        var p95 = latencies[(int)Math.Ceiling((latencies.Length - 1) * 0.95)];
        var report = new BenchmarkReport(
            "Atomic-Write (Decode + .tmp + Move)",
            files.Count,
            totalSec,
            throughput,
            median,
            p95,
            latencies[0],
            latencies[latencies.Length - 1],
            errCount);

        Console.WriteLine($"    {throughput:N0} thumbs/s, med {median:N2} ms, p95 {p95:N2} ms, Errors: {errCount}");

        var thumbsExist = Directory.GetFiles(thumbsDir, "*.jpg").Length;
        var partial = Directory.GetFiles(thumbsDir, "*.tmp").Length;
        Console.WriteLine($"    Thumbs persisted: {thumbsExist}, halbfertig (.tmp): {partial}");
        return report;
    }

    private static void PrintSummary(IReadOnlyList<BenchmarkReport> reports, Spike04Options options)
    {
        Console.WriteLine($"=== Spike #04 Summary ({options.Count} JPGs) ===");
        Console.WriteLine();
        Console.WriteLine($"    {"Konfiguration",-50} {"Total",8} {"Thumbs/s",10} {"Median",10} {"p95",10} {"Errors",8} Ampel");
        Console.WriteLine($"    {new string('-', 50)} {new string('-', 8)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 8)} -----");
        foreach (var r in reports)
        {
            Console.WriteLine($"    {Truncate(r.Name, 50),-50} {r.TotalSeconds,6:N2} s {r.Throughput,8:N0}/s {r.MedianMs,7:N2} ms {r.P95Ms,7:N2} ms {r.Errors,8} {r.AmpelFor(30, 100)}");
        }

        Console.WriteLine();
        Console.WriteLine("Ziele: Throughput >= 100 thumbs/s, Median < 30 ms, p95 < 100 ms, Errors = 0.");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static Spike04Options ParseArgs(string[] args)
    {
        var count = DefaultCount;
        var thumbWidth = DefaultThumbWidth;
        var workers = Math.Max(2, Environment.ProcessorCount - 2);
        string? spikeDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--count" when i + 1 < args.Length:
                    count = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--thumb-width" when i + 1 < args.Length:
                    thumbWidth = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--workers" when i + 1 < args.Length:
                    workers = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--dir" when i + 1 < args.Length:
                    spikeDir = args[++i];
                    break;
            }
        }

        spikeDir ??= BuildDefaultSpikeDir();
        return new Spike04Options(count, thumbWidth, workers, spikeDir);
    }

    private static string BuildDefaultSpikeDir()
    {
        var paths = new AppDataPaths();
        return Path.Combine(paths.AppDataRoot, SpikeFolderName);
    }

    private sealed record Spike04Options(int Count, int ThumbWidth, int ParallelWorkers, string SpikeDir);
}
