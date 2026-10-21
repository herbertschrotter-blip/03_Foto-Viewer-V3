using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lumora.Services.Thumbnails;

/// <summary>
/// FFmpeg-Wrapper für Thumbnails: Frame-Extraktion aus Videos (<see cref="IVideoThumbnailRenderer"/>)
/// und Bild-Fallback für Formate ohne WIC-Codec (<see cref="IFallbackImageThumbnailRenderer"/>:
/// WebP/HEIC/AVIF/Kamera-RAW). Eigener ProcessStartInfo-Wrapper gemäß ADR-0006 (kein
/// FFMpegCore-NuGet). Timeout via <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
/// + harter <c>Process.Kill(entireProcessTree)</c> — Pattern aus Spike #5, gegen Hänger bei
/// kaputten Dateien. Argumente über <see cref="ProcessStartInfo.ArgumentList"/> (korrektes Quoting
/// für Leerzeichen, Sonderzeichen, nicht-ASCII), stdout wird mitgelesen (kein Pipe-Deadlock).
/// Videos kürzer als 1 s liefern bei <c>-ss 1</c> kein Frame → zweiter Lauf ohne Seek.
/// Schreibt atomar (.tmp + Move), sodass nie ein halbfertiges Thumbnail sichtbar wird.
/// Größe wie beim WIC-Renderer (ADR-0043): kurze Kante = Zielgröße, lange höchstens das Dreifache, nie hochskaliert
/// (Ausdruck im <c>scale</c>-Filter, nach der automatischen Drehung). Die Maße des Originals liest
/// <see cref="FfmpegStreamInfo"/> aus der Eingabe-Beschreibung auf stderr (<c>-loglevel info</c>).
/// </summary>
public sealed class FfmpegVideoThumbnailer : IVideoThumbnailRenderer, IFallbackImageThumbnailRenderer
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

    /// <summary>Video: Frame bei 1 s (vermeidet schwarze Intro-Frames); bei Clips unter 1 s Fallback auf das erste Frame.</summary>
    public Task<ThumbnailRenderResult> RenderAsync(
        string sourcePath,
        string targetThumbPath,
        int targetShortEdge,
        CancellationToken cancellationToken)
        => RunAsync(sourcePath, targetThumbPath, targetShortEdge, seekIntoStream: true, cancellationToken);

    /// <summary>
    /// Bild-Fallback: erstes (einziges) Frame ohne <c>-ss</c> — ein Standbild hat bei 1 s nichts
    /// mehr zu liefern. Gleiche Timeout-/Kill-Mechanik und atomares Schreiben wie beim Video.
    /// </summary>
    public Task<ThumbnailRenderResult> RenderImageAsync(
        string sourcePath,
        string targetThumbPath,
        int targetShortEdge,
        CancellationToken cancellationToken)
        => RunAsync(sourcePath, targetThumbPath, targetShortEdge, seekIntoStream: false, cancellationToken);

    Task<ThumbnailRenderResult> IFallbackImageThumbnailRenderer.RenderAsync(
        string sourcePath,
        string targetThumbPath,
        int targetShortEdge,
        CancellationToken cancellationToken)
        => RenderImageAsync(sourcePath, targetThumbPath, targetShortEdge, cancellationToken);

    private async Task<ThumbnailRenderResult> RunAsync(
        string sourcePath,
        string targetThumbPath,
        int targetShortEdge,
        bool seekIntoStream,
        CancellationToken cancellationToken)
    {
        var ffmpeg = _ffmpegPath.Value;
        if (ffmpeg is null)
        {
            return ThumbnailRenderResult.Fail(
                ThumbnailErrorCodes.FfmpegNotFound,
                "FFmpeg wurde nicht gefunden (weder im PATH noch unter winget-Links).");
        }

        if (!File.Exists(sourcePath))
        {
            return ThumbnailRenderResult.Fail(ThumbnailErrorCodes.SourceMissing, $"Quelldatei fehlt: {sourcePath}");
        }

        var tempPath = targetThumbPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(targetThumbPath)!);

        var result = await ExecuteAsync(ffmpeg, sourcePath, tempPath, targetShortEdge, seekIntoStream, cancellationToken).ConfigureAwait(false);
        if (!result.Success && seekIntoStream && result.ErrorCode == ThumbnailErrorCodes.FfmpegFailed)
        {
            // Clip kürzer als der Seek (oder Seek im Container nicht möglich): erstes Frame nehmen.
            _logger.LogDebug("FFmpeg ohne Frame bei -ss 1 ({Path}) — zweiter Lauf ohne Seek", sourcePath);
            result = await ExecuteAsync(ffmpeg, sourcePath, tempPath, targetShortEdge, seekIntoStream: false, cancellationToken).ConfigureAwait(false);
        }

        if (!result.Success)
        {
            TryDelete(tempPath);
            return result;
        }

        File.Move(tempPath, targetThumbPath, overwrite: true);
        var info = new FileInfo(targetThumbPath);
        _logger.LogDebug(
            "{Kind}-Thumb via FFmpeg erzeugt: {Path} ({Bytes} B)",
            seekIntoStream ? "Video" : "Bild", targetThumbPath, info.Length);

        // Pixelmaße des Vorschaubilds werden ohne Re-Decode nicht ermittelt → 0/0; ByteSize und Originalmaße sind gesetzt.
        return ThumbnailRenderResult.Ok(0, 0, info.Length) with { SourceWidth = result.SourceWidth, SourceHeight = result.SourceHeight };
    }

    /// <summary>Ein FFmpeg-Lauf; bei Erfolg liegt das Frame in <paramref name="tempPath"/> (noch nicht an den Zielpfad verschoben).</summary>
    private async Task<ThumbnailRenderResult> ExecuteAsync(
        string ffmpeg,
        string sourcePath,
        string tempPath,
        int targetShortEdge,
        bool seekIntoStream,
        CancellationToken cancellationToken)
    {
        TryDelete(tempPath);

        // -ss 1              : (nur Video) 1 s in den Stream springen (vermeidet schwarzes Intro-Frame)
        // -frames:v 1        : genau ein Frame greifen
        // -loglevel info     : Eingabe-Beschreibung (Maße, Drehung) für Files.Width/Height; -nostats ohne Fortschritt
        // scale=…            : kurze Kante auf targetShortEdge, lange höchstens das Dreifache, nie hochskalieren
        //                      (FFmpeg dreht vorher automatisch → iw/ih sind die angezeigten Maße); Höhe gerade
        // -f image2 -update 1: Einzelbild-Muxer explizit — die .tmp-Endung lässt FFmpeg sonst
        //                      keinen Ausgabeformat-Rückschluss zu ("Unable to choose an output format")
        // -c:v mjpeg -q:v 4  : JPG-Encoder + Qualität (niedriger = besser, 2–5 typisch)
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("info");
        psi.ArgumentList.Add("-y");
        if (seekIntoStream)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add("1");
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add(BuildScaleFilter(targetShortEdge));
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("image2");
        psi.ArgumentList.Add("-update");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("mjpeg");
        psi.ArgumentList.Add("-q:v");
        psi.ArgumentList.Add("4");
        psi.ArgumentList.Add(tempPath);

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
        proc.OutputDataReceived += static (_, _) => { }; // stdout nur leeren — sonst blockiert FFmpeg bei vollem Pipe-Puffer

        var cancelled = false;
        try
        {
            if (!proc.Start())
            {
                return ThumbnailRenderResult.Fail(ThumbnailErrorCodes.FfmpegStartFailed, "Process.Start lieferte false.");
            }

            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                KillQuietly(proc);
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
            return ThumbnailRenderResult.Fail(ThumbnailErrorCodes.FfmpegException, ex.Message);
        }

        sw.Stop();
        var stderrText = stderr.ToString();
        var tail = TrimTail(stderrText);

        if (cancelled)
        {
            TryDelete(tempPath);
            return cancellationToken.IsCancellationRequested
                ? ThumbnailRenderResult.Fail(ThumbnailErrorCodes.FfmpegCancelled, $"Abgebrochen. {tail}")
                : ThumbnailRenderResult.Fail(ThumbnailErrorCodes.FfmpegTimeout, $"Timeout nach {FrameTimeout.TotalSeconds:N0} s. {tail}");
        }

        var produced = File.Exists(tempPath) && new FileInfo(tempPath).Length > 0;
        if (proc.ExitCode != 0 || !produced)
        {
            TryDelete(tempPath);
            var detail = proc.ExitCode == 0 ? "kein Frame erzeugt" : $"ExitCode={proc.ExitCode}";
            return ThumbnailRenderResult.Fail(ThumbnailErrorCodes.FfmpegFailed, $"{detail}. {tail}");
        }

        _logger.LogDebug("FFmpeg-Lauf ({Seek}) für {Path} in {Ms:N0} ms", seekIntoStream ? "-ss 1" : "ohne Seek", sourcePath, sw.Elapsed.TotalMilliseconds);
        var size = FfmpegStreamInfo.TryParseDisplaySize(stderrText);
        return ThumbnailRenderResult.Ok(0, 0, new FileInfo(tempPath).Length) with
        {
            SourceWidth = size?.Width ?? 0,
            SourceHeight = size?.Height ?? 0,
        };
    }

    /// <summary>
    /// <c>scale</c>-Filter: Faktor = min(1, kurz/min(iw,ih), 3·kurz/max(iw,ih)); Breite auf gerade Zahl gerundet, Höhe proportional
    /// und gerade (-2). Einfache Anführungszeichen schützen die Kommas vor dem Filtergraph-Parser.
    /// </summary>
    public static string BuildScaleFilter(int shortEdge)
    {
        var maxLong = ThumbnailSizing.MaxLongEdge(shortEdge);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"scale=w='max(2,round(iw*min(1,min({shortEdge}/min(iw,ih),{maxLong}/max(iw,ih)))/2)*2)':h=-2");
    }

    private static void KillQuietly(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort: Prozess ist evtl. gerade beendet
        }
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

    /// <summary>Kopf- und Statuszeilen von <c>-loglevel info</c>, die keinen Fehler beschreiben.</summary>
    public static bool IsInfoLine(string line)
        => char.IsWhiteSpace(line[0])
           || line.StartsWith("frame=", StringComparison.Ordinal)
           || line.StartsWith("Input #", StringComparison.Ordinal)
           || line.StartsWith("Output #", StringComparison.Ordinal)
           || line.StartsWith("Stream mapping", StringComparison.Ordinal)
           || line.StartsWith("Press [q]", StringComparison.Ordinal)
           || line.Contains("muxing overhead", StringComparison.Ordinal);

    /// <summary>Letzte Zeilen von stderr ohne die Beschreibungs- und Statuszeilen von <c>-loglevel info</c>.</summary>
    private static string TrimTail(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "(stderr leer)";
        }

        var lines = raw.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !IsInfoLine(l))
            .ToArray();
        if (lines.Length == 0)
        {
            return "(stderr ohne Fehlermeldung)";
        }

        return lines.Length <= StdErrTailLines
            ? string.Join(" | ", lines)
            : string.Join(" | ", lines[^StdErrTailLines..]);
    }
}
