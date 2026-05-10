using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Spike03.Services;

public sealed record SpikeMediaInfo(string LocalPath, long SizeBytes, bool WasDownloaded);

public sealed record DownloadProgress(long Bytes, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0.0 : (double)Bytes / TotalBytes;
}

/// <summary>
/// Lädt ein öffentliches H.264 Sample-MP4 (Big Buck Bunny) einmalig nach
/// %LocalAppData%\PhotoViewerV3\spike-data\BigBuckBunny.mp4. Idempotent — bei
/// vorhandener Datei mit plausibler Größe wird der Download übersprungen.
/// Atomic write via .tmp + Move.
/// </summary>
public sealed class SpikeMediaDownloader : IDisposable
{
    // Sintel Trailer (Blender Foundation, CC-BY) — 4,4 MB H.264 480p, ~52 s.
    // Klein genug für schnellen Spike-Download, groß genug für Player-Lifecycle-Tests.
    private const string SampleUrl = "https://download.blender.org/durian/trailer/sintel_trailer-480p.mp4";
    private const string SampleFileName = "sintel_trailer-480p.mp4";
    private const string SpikeDataFolderName = "spike-data";

    // Datei ist ~4,3 MB; alles unter 1 MB ist mit Sicherheit kaputt/abgebrochen
    private const long MinValidSize = 1L * 1024 * 1024;

    private readonly IAppDataPaths _paths;
    private readonly HttpClient _http;

    public SpikeMediaDownloader(IAppDataPaths paths)
    {
        _paths = paths;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public string ExpectedLocalPath =>
        Path.Combine(_paths.AppDataRoot, SpikeDataFolderName, SampleFileName);

    public Uri SourceUri => new(SampleUrl);

    public bool IsLocallyAvailable
    {
        get
        {
            var path = ExpectedLocalPath;
            if (!File.Exists(path))
            {
                return false;
            }

            return new FileInfo(path).Length >= MinValidSize;
        }
    }

    public async Task<SpikeMediaInfo> EnsureSampleAsync(
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var path = ExpectedLocalPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            if (info.Length >= MinValidSize)
            {
                return new SpikeMediaInfo(path, info.Length, WasDownloaded: false);
            }

            // Vorhandene Datei ist zu klein → vermutlich abgebrochener Download
            File.Delete(path);
        }

        var tempPath = path + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        using var response = await _http
            .GetAsync(SampleUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? -1L;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dest = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true))
        {
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;
                progress?.Report(new DownloadProgress(copied, totalBytes));
            }
        }

        File.Move(tempPath, path, overwrite: true);
        return new SpikeMediaInfo(path, new FileInfo(path).Length, WasDownloaded: true);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
