using System.IO.Compression;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Services.Faces.Models;

/// <summary>Zustand der Modell-Dateien.</summary>
public enum FaceModelState
{
    /// <summary>Mindestens eine Datei fehlt.</summary>
    Missing = 0,

    /// <summary>Beide Dateien vorhanden.</summary>
    Ready = 1,
}

/// <summary>Bezugsquelle des Modell-Archivs: Adresse, erwartete Prüfsumme und Größe (Anzeige, Plausibilität).</summary>
public sealed record FaceModelDownloadInfo(Uri Source, string Sha256, long Size)
{
    /// <summary>
    /// InsightFace buffalo_l (Release v0.7, 288 621 354 Bytes) — Lizenz der Modelle: nur nicht-kommerzielle Nutzung.
    /// Prüfsumme vom Benutzer-Download am 2026-10-21.
    /// </summary>
    public static FaceModelDownloadInfo BuffaloL { get; } = new(
        new Uri("https://github.com/deepinsight/insightface/releases/download/v0.7/buffalo_l.zip"),
        "80ffe37d8a5940d59a7384c201a2a38d4741f2f3c51eef46ebb28218a7b0ca2f",
        288_621_354);
}

/// <summary>Fortschritt des Downloads (Bytes) bzw. der Phase danach.</summary>
public sealed record FaceModelDownloadProgress(string Stage, long Done, long Total);

/// <summary>Die Modell-Dateien wurden heruntergeladen oder haben sich sonst geändert (Analysator neu laden).</summary>
public sealed record FaceModelChangedMessage(FaceModelState State);

/// <summary>Download oder Prüfung des Modell-Archivs ist gescheitert (Text für den Benutzer).</summary>
public sealed class FaceModelDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Modellverwaltung (ADR-0035): Ordner <c>&lt;AppData&gt;\models\buffalo_l\</c> mit <c>det_10g.onnx</c> und
/// <c>w600k_r50.onnx</c>; die Umgebungsvariable <see cref="OverrideVariable"/> zeigt für Entwicklung und Tests auf
/// einen anderen Ordner (dann kein Download).
/// </summary>
public interface IFaceModelProvider
{
    string ModelFolder { get; }

    /// <summary>true, wenn <see cref="OverrideVariable"/> den Ordner vorgibt.</summary>
    bool IsOverridden { get; }

    string DetectorPath { get; }

    string RecognizerPath { get; }

    FaceModelState State { get; }

    FaceModelDownloadInfo DownloadInfo { get; }

    /// <summary>
    /// Lädt das Archiv herunter (nur nach Benutzerklick aufrufen), prüft SHA-256 und entpackt NUR die beiden benötigten
    /// Dateien. Abbrechbar; bei Abbruch oder Fehler bleibt kein halbes Modell liegen. Sendet danach
    /// <see cref="FaceModelChangedMessage"/>.
    /// </summary>
    Task DownloadAsync(IProgress<FaceModelDownloadProgress>? progress, CancellationToken ct);

    public const string OverrideVariable = "PHOTOVIEWERV3_FACE_MODELS";
    public const string DetectorFileName = "det_10g.onnx";
    public const string RecognizerFileName = "w600k_r50.onnx";
}

/// <inheritdoc />
public sealed class FaceModelProvider : IFaceModelProvider
{
    private const int BufferSize = 1 << 16;

    private readonly ILogger _logger;
    private readonly IMessenger _messenger;
    private readonly HttpClient? _http;

    /// <param name="downloadInfo">Tests: eigene Quelle (auch <c>file://</c>) und Prüfsumme.</param>
    /// <param name="modelFolder">Tests: fester Ordner statt AppData/Umgebungsvariable.</param>
    public FaceModelProvider(
        IAppDataPaths paths,
        ILogger<FaceModelProvider>? logger = null,
        IMessenger? messenger = null,
        FaceModelDownloadInfo? downloadInfo = null,
        string? modelFolder = null,
        HttpClient? http = null)
    {
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _http = http;
        DownloadInfo = downloadInfo ?? FaceModelDownloadInfo.BuffaloL;
        var overridden = Environment.GetEnvironmentVariable(IFaceModelProvider.OverrideVariable);
        if (!string.IsNullOrWhiteSpace(modelFolder))
        {
            ModelFolder = Path.GetFullPath(modelFolder);
        }
        else if (!string.IsNullOrWhiteSpace(overridden))
        {
            ModelFolder = Path.GetFullPath(overridden);
            IsOverridden = true;
        }
        else
        {
            ModelFolder = Path.Combine(paths.AppDataRoot, "models", "buffalo_l");
        }
    }

    public string ModelFolder { get; }

    public bool IsOverridden { get; }

    public string DetectorPath => Path.Combine(ModelFolder, IFaceModelProvider.DetectorFileName);

    public string RecognizerPath => Path.Combine(ModelFolder, IFaceModelProvider.RecognizerFileName);

    public FaceModelDownloadInfo DownloadInfo { get; }

    public FaceModelState State
        => File.Exists(DetectorPath) && File.Exists(RecognizerPath) ? FaceModelState.Ready : FaceModelState.Missing;

    public async Task DownloadAsync(IProgress<FaceModelDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelFolder);
        var archive = Path.Combine(ModelFolder, "buffalo_l.zip.download");
        try
        {
            _logger.LogInformation("Gesichtserkennung: Modell-Download gestartet ({Source})", DownloadInfo.Source);
            await DownloadArchiveAsync(archive, progress, ct).ConfigureAwait(false);

            progress?.Report(new FaceModelDownloadProgress("Prüfsumme wird geprüft", 0, 0));
            var hash = await ComputeSha256Async(archive, ct).ConfigureAwait(false);
            if (!string.Equals(hash, DownloadInfo.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new FaceModelDownloadException(
                    $"Die Prüfsumme des heruntergeladenen Archivs stimmt nicht (erwartet {DownloadInfo.Sha256}, erhalten {hash}). Das Archiv wurde verworfen.");
            }

            progress?.Report(new FaceModelDownloadProgress("Modelle werden entpackt", 0, 0));
            await Task.Run(() => ExtractModels(archive, ct), ct).ConfigureAwait(false);
            _logger.LogInformation("Gesichtserkennung: Modell heruntergeladen, geprüft und entpackt nach {Folder}", ModelFolder);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Gesichtserkennung: Modell-Download abgebrochen");
            throw;
        }
        catch (FaceModelDownloadException ex)
        {
            _logger.LogWarning("Gesichtserkennung: Modell-Download gescheitert: {Message}", ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Gesichtserkennung: Modell-Download gescheitert");
            throw new FaceModelDownloadException($"Das Modell konnte nicht geladen werden: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(archive);
        }

        _messenger.Send(new FaceModelChangedMessage(State));
    }

    private async Task DownloadArchiveAsync(string target, IProgress<FaceModelDownloadProgress>? progress, CancellationToken ct)
    {
        Stream source;
        long total = DownloadInfo.Size;
        HttpResponseMessage? response = null;
        HttpClient? ownClient = null;
        if (DownloadInfo.Source.IsFile)
        {
            source = new FileStream(DownloadInfo.Source.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            total = source.Length;
        }
        else
        {
            var client = _http ?? (ownClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
            response = await client.GetAsync(DownloadInfo.Source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            total = response.Content.Headers.ContentLength ?? total;
            source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            var buffer = new byte[BufferSize];
            long done = 0;
            var lastReport = 0L;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (done - lastReport >= 1 << 20)
                {
                    lastReport = done;
                    progress?.Report(new FaceModelDownloadProgress("Wird heruntergeladen", done, total));
                }
            }

            progress?.Report(new FaceModelDownloadProgress("Wird heruntergeladen", done, total));
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
            response?.Dispose();
            ownClient?.Dispose();
        }
    }

    /// <summary>SHA-256 einer Datei als Kleinbuchstaben-Hex.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Entpackt nur die beiden benötigten Einträge (Name ohne Ordner, Groß/Klein egal) über .tmp + Umbenennen; die
    /// übrigen Modelle des Archivs (1k3d68, 2d106det, genderage) bleiben draußen.
    /// </summary>
    private void ExtractModels(string archive, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        var wanted = new[] { IFaceModelProvider.DetectorFileName, IFaceModelProvider.RecognizerFileName };
        var temps = new List<(string Temp, string Final)>();
        try
        {
            foreach (var name in wanted)
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new FaceModelDownloadException($"Das Archiv enthält {name} nicht.");
                var final = Path.Combine(ModelFolder, name);
                var temp = final + ".tmp";
                entry.ExtractToFile(temp, overwrite: true);
                temps.Add((temp, final));
            }

            foreach (var (temp, final) in temps)
            {
                File.Move(temp, final, overwrite: true);
            }
        }
        finally
        {
            foreach (var (temp, _) in temps)
            {
                TryDelete(temp);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Temporäre Datei konnte nicht gelöscht werden: {Path}", path);
        }
    }
}
