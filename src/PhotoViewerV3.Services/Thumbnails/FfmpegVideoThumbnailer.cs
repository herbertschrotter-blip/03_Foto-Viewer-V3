using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace PhotoViewerV3.Services.Thumbnails;

/// <summary>
/// Erzeugt Video-Thumbnails durch Frame-Extraktion via FFmpeg. Eigener
/// ProcessStartInfo-Wrapper gemäß ADR-0006 (kein FFMpegCore-NuGet). Timeout via
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> + harter
/// <c>Process.Kill(entireProcessTree)</c> — Pattern aus Spike #5, gegen Hänger bei
/// kaputten Videos. Schreibt atomar (.tmp + Move), sodass nie ein halbfertiges
/// Thumbnail sichtbar wird.
/// </summary>
public sealed class FfmpegVideoThumbnailer : IVideoThumbnailRenderer
{
    private const int StdErrTailLines = 6;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    private readonly ILogger<FfmpegVideoThumbnailer> _logger;
    private readonly Lazy<string?> _ffmpegPath;

    public FfmpegVideoThumbnailer(ILogger<FfmpegVideoThumbnailer> logger)
    {
        _logger = logger;
        _ffmpegPath = new Lazy<string?>(TryLocateFfmpeg, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => _ffmpegPath.Value is not null;

    public async Task<ThumbnailRenderResult> RenderAsync(
        string sourcePath,
        string targetThumbPath,
        int targetWidth,
        CancellationToken cancellationToken)
    {
        var ffmpeg = _ffmpegPath.Value;
        if (ffmpeg is null)
        {
            return ThumbnailRenderResult.Fail(
                "ffmpeg_not_found",
                "FFmpeg wurde nicht gefunden (weder im PATH noch unter winget-Links).");
        }

        if (!File.Exists(sourcePath))
        {
            return ThumbnailRenderResult.Fail("source_missing", $"Quelldatei fehlt: {sourcePath}");
        }

        var tempPath = targetThumbPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(targetThumbPath)!);
        TryDelete(tempPath);

        // -ss 1     : 1 s in das Video springen (vermeidet schwarzes Intro-Frame)
        // -frames:v 1: genau ein Frame greifen
        // scale=w:-1: Breite auf targetWidth begrenzen, Höhe proportional
        // -q:v 4    : JPG-Qualität (niedriger = besser, 2–5 typisch)
        var args = $"-hide_banner -loglevel error -y -ss 1 -i \"{sourcePath}\" " +
                   $"-frames:v 1 -vf scale={targetWidth}:-1 -q:v 4 \"{tempPath}\"";

        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FrameTimeout);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        var cancelled = false;
        try
        {
            if (!proc.Start())
            {
                return ThumbnailRenderResult.Fail("ffmpeg_start_failed", "Process.Start lieferte false.");
            }

            proc.BeginErrorReadLine();

            await using var registration = timeoutCts.Token.Register(() =>
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
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return ThumbnailRenderResult.Fail("ffmpeg_exception", ex.Message);
        }

        sw.Stop();
        var tail = TrimTail(stderr.ToString());

        if (cancelled)
        {
            TryDelete(tempPath);
            var reason = cancellationToken.IsCancellationRequested
                ? "abgebrochen"
                : $"Timeout nach {FrameTimeout.TotalSeconds:N0} s";
            return ThumbnailRenderResult.Fail("ffmpeg_cancelled", $"{reason}. {tail}");
        }

        if (proc.ExitCode != 0 || !File.Exists(tempPath))
        {
            TryDelete(tempPath);
            return ThumbnailRenderResult.Fail("ffmpeg_failed", $"ExitCode={proc.ExitCode}. {tail}");
        }

        File.Move(tempPath, targetThumbPath, overwrite: true);
        var info = new FileInfo(targetThumbPath);
        _logger.LogDebug(
            "Video-Thumb erzeugt: {Path} ({Bytes} B) in {Ms:N0} ms",
            targetThumbPath, info.Length, sw.Elapsed.TotalMilliseconds);

        // Pixelmaße werden ohne Re-Decode nicht ermittelt → 0/0; ByteSize ist gesetzt.
        return ThumbnailRenderResult.Ok(0, 0, info.Length);
    }

    private static string? TryLocateFfmpeg()
    {
        // 1) PATH-Lookup via cmd 'where'
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
                foreach (var path in output.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries))
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
            // ignore — try fallback
        }

        // 2) winget-Standard-Link-Pfad
        var wingetLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        return File.Exists(wingetLink) ? wingetLink : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static string TrimTail(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "(stderr leer)";
        }

        var lines = raw.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length <= StdErrTailLines
            ? string.Join(" | ", lines)
            : string.Join(" | ", lines[^StdErrTailLines..]);
    }
}
