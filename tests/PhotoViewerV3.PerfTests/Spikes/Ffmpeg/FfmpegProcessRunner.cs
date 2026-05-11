using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoViewerV3.PerfTests.Spikes.Ffmpeg;

public sealed record FfmpegResult(
    bool Success,
    bool Cancelled,
    int ExitCode,
    string StdErrTail,
    TimeSpan Duration);

/// <summary>
/// Eigener ProcessStartInfo-Wrapper für FFmpeg gemäß ADR-0006 und PLAN Kap. 2
/// (kein FFMpegCore-NuGet). Cancellation via Process.Kill(entireProcessTree:true)
/// bei externem Token oder hartem CancelAfter-Timeout.
/// </summary>
public static class FfmpegProcessRunner
{
    private const int StdErrTailLines = 8;
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    public static string? TryLocateFfmpeg()
    {
        // 1. PATH-Lookup via cmd 'where'
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c where ffmpeg.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);
                var first = output.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries);
                foreach (var path in first)
                {
                    var trimmed = path.Trim();
                    if (File.Exists(trimmed))
                    {
                        return trimmed;
                    }
                }
            }
        }
        catch
        {
            // ignore — try fallbacks
        }

        // 2. winget standard link path
        var wingetLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(wingetLink))
        {
            return wingetLink;
        }

        return null;
    }

    public static async Task<FfmpegResult> ExtractStillFrameAsync(
        string ffmpegExe,
        string inputPath,
        string outputJpgPath,
        TimeSpan timeout,
        CancellationToken externalCt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputJpgPath)!);
        if (File.Exists(outputJpgPath))
        {
            File.Delete(outputJpgPath);
        }

        // -ss 1 = skip 1s into video, -frames:v 1 = grab one frame, -y = overwrite
        var args = $"-hide_banner -loglevel error -y -ss 1 -i \"{inputPath}\" -frames:v 1 -q:v 4 \"{outputJpgPath}\"";

        var psi = new ProcessStartInfo(ffmpegExe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        timeoutCts.CancelAfter(timeout);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderrBuffer = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderrBuffer)
                {
                    stderrBuffer.AppendLine(e.Data);
                }
            }
        };

        if (!proc.Start())
        {
            sw.Stop();
            return new FfmpegResult(false, false, -1, "Process.Start lieferte false", sw.Elapsed);
        }

        proc.BeginErrorReadLine();

        var cancelled = false;
        await using var ctRegistration = timeoutCts.Token.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                {
                    cancelled = true;
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best-effort
            }
        });

        try
        {
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        sw.Stop();

        // Grace-Wait für Kill, falls noch nicht durch
        if (cancelled)
        {
            try
            {
                proc.WaitForExit((int)KillGrace.TotalMilliseconds);
            }
            catch
            {
                // best-effort
            }
        }

        var stderrTail = TrimStdErrTail(stderrBuffer.ToString());
        var success = !cancelled && proc.ExitCode == 0 && File.Exists(outputJpgPath);
        return new FfmpegResult(success, cancelled, proc.ExitCode, stderrTail, sw.Elapsed);
    }

    private static string TrimStdErrTail(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "(leer)";
        }

        var lines = raw.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= StdErrTailLines)
        {
            return string.Join(" | ", lines);
        }

        return string.Join(" | ", lines[^StdErrTailLines..]);
    }
}
