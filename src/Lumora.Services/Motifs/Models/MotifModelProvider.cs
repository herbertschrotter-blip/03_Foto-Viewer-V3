using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Infrastructure;

namespace Lumora.Services.Motifs.Models;

/// <summary>Eine Datei des Motiv-Modells: Pfad relativ zum Modellordner (mit „/“), Größe, SHA-256.</summary>
public sealed record MotifModelFile(string RelativePath, long Size, string Sha256, bool Required)
{
    public string LocalRelativePath => RelativePath.Replace('/', Path.DirectorySeparatorChar);
}

/// <summary>Bezugsquelle des Motiv-Modells: Basisadresse, Dateien mit Prüfsummen, Lizenz.</summary>
public sealed record MotifModelDownloadInfo(Uri BaseUri, IReadOnlyList<MotifModelFile> Files, string License)
{
    public const string VisionModel = "onnx/vision_model.onnx";
    public const string TextModel = "onnx/text_model_int8.onnx";
    public const string Tokenizer = "tokenizer.json";

    /// <summary>
    /// SigLIP 2 base-patch16-224 als ONNX (Hugging Face onnx-community, Apache-2.0). Größen und SHA-256 der großen
    /// Dateien aus der Hugging-Face-API (LFS-Prüfsumme, 2026-10-21), die der kleinen JSON-Dateien aus dem von Herbert
    /// geprüften Download (Git-Prüfsumme gegen die API abgeglichen). Bildmodell fp32 (Grafikkarte; fp16 ist auf der
    /// GTX 1080 Ti langsam), Sprachmodell int8 (Prozessor).
    /// </summary>
    public static MotifModelDownloadInfo Siglip2Base { get; } = new(
        new Uri("https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/main/"),
        [
            new(VisionModel, 371_807_752, "c0573e3f4140c3a7c4e9cc5912bd6b26a033b46a6a8e8af26cbea262b163bcad", true),
            new(TextModel, 283_438_275, "3a0603d3a00c05a80a6ded4743c16aaac7b1e62cdcc7e362e7ce418659b96400", true),
            new(Tokenizer, 34_363_039, "cb9140fae3ac5122c972d37adf83e1248471a38147ad76f8215c8872c6fd8322", true),
            new("config.json", 435, "e43a9f7692d3819886a82cb2097048258d444f123c67d37ec825f9345b019cf2", false),
            new("preprocessor_config.json", 394, "9b36b57ebaf20f09bf4c22100ccc21877ea6bfe5aead0c00c59f8af8ccefacfc", false),
            new("tokenizer_config.json", 47_240, "7c3a247599e741bceba1a3fe0285aea88d1044dc1fad2caa1e48cdd9fd25f630", false),
            new("special_tokens_map.json", 636, "baec30ea10906f16adb8c18af7a34023002c1746542612b8b41c9f09e1351351", false),
        ],
        "Apache-2.0");

    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>Zustand der Modell-Dateien.</summary>
public enum MotifModelState
{
    /// <summary>Mindestens eine benötigte Datei fehlt.</summary>
    Missing = 0,

    /// <summary>Bildmodell, Sprachmodell und Tokenizer vorhanden.</summary>
    Ready = 1,
}

/// <summary>Fortschritt des Downloads (Bytes über alle Dateien) bzw. der Phase danach.</summary>
public sealed record MotifModelDownloadProgress(string Stage, long Done, long Total);

/// <summary>Die Modell-Dateien wurden heruntergeladen oder haben sich sonst geändert (Encoder neu laden).</summary>
public sealed record MotifModelChangedMessage(MotifModelState State);

/// <summary>Download oder Prüfung ist gescheitert (Text für den Benutzer).</summary>
public sealed class MotifModelDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Modellverwaltung der Motiv-Erkennung (ADR-0052, Muster ADR-0035): Ordner
/// <c>&lt;AppData&gt;\models\siglip2-base-patch16-224\</c> (außerhalb von Repo und portabler Version); die
/// Umgebungsvariable <see cref="OverrideVariable"/> zeigt für Entwicklung und Tests auf einen anderen Ordner (dann kein
/// Download).
/// </summary>
public interface IMotifModelProvider
{
    public const string OverrideVariable = "LUMORA_MOTIF_MODEL";
    public const string FolderName = "siglip2-base-patch16-224";

    string ModelFolder { get; }

    bool IsOverridden { get; }

    MotifModelState State { get; }

    MotifModelDownloadInfo DownloadInfo { get; }

    string PathOf(string relativePath);

    /// <summary>
    /// Lädt die fehlenden Dateien herunter (nur nach Benutzerklick und Bestätigung), prüft je Datei SHA-256 und
    /// verschiebt sie erst danach an ihren Platz. Abbrechbar; bei Abbruch oder Fehler bleibt keine halbe Datei liegen.
    /// Sendet danach <see cref="MotifModelChangedMessage"/>.
    /// </summary>
    Task DownloadAsync(IProgress<MotifModelDownloadProgress>? progress, CancellationToken ct);
}

/// <inheritdoc />
public sealed class MotifModelProvider : IMotifModelProvider
{
    private const int BufferSize = 1 << 16;

    private readonly ILogger _logger;
    private readonly IMessenger _messenger;
    private readonly HttpClient? _http;

    /// <param name="downloadInfo">Tests: eigene Quelle und Prüfsummen.</param>
    /// <param name="modelFolder">Tests: fester Ordner statt AppData/Umgebungsvariable.</param>
    /// <param name="http">Tests: HttpClient mit Fake-Handler.</param>
    public MotifModelProvider(
        IAppDataPaths paths,
        ILogger<MotifModelProvider>? logger = null,
        IMessenger? messenger = null,
        MotifModelDownloadInfo? downloadInfo = null,
        string? modelFolder = null,
        HttpClient? http = null)
    {
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _http = http;
        DownloadInfo = downloadInfo ?? MotifModelDownloadInfo.Siglip2Base;
        var overridden = Environment.GetEnvironmentVariable(IMotifModelProvider.OverrideVariable);
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
            ModelFolder = Path.Combine(paths.AppDataRoot, "models", IMotifModelProvider.FolderName);
        }
    }

    public string ModelFolder { get; }

    public bool IsOverridden { get; }

    public MotifModelDownloadInfo DownloadInfo { get; }

    public MotifModelState State
        => DownloadInfo.Files.Where(f => f.Required).All(f => File.Exists(PathOf(f.RelativePath)))
            ? MotifModelState.Ready
            : MotifModelState.Missing;

    public string PathOf(string relativePath)
        => Path.Combine(ModelFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public async Task DownloadAsync(IProgress<MotifModelDownloadProgress>? progress, CancellationToken ct)
    {
        // Vorhandene Dateien mit passender Größe bleiben (ein abgebrochener Download setzt so fort).
        var pending = DownloadInfo.Files
            .Where(f => !File.Exists(PathOf(f.RelativePath)) || new FileInfo(PathOf(f.RelativePath)).Length != f.Size)
            .ToList();
        var total = pending.Sum(f => f.Size);
        long done = 0;
        _logger.LogInformation("Motive: Modell-Download gestartet ({Count} Dateien, {MB:N0} MB, {Source})", pending.Count, total / 1048576.0, DownloadInfo.BaseUri);
        HttpClient? ownClient = null;
        var client = _http ?? (ownClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        try
        {
            foreach (var file in pending)
            {
                var final = PathOf(file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                var temp = final + ".download";
                try
                {
                    var before = done;
                    await DownloadFileAsync(client, file, temp, d => progress?.Report(new MotifModelDownloadProgress("Wird heruntergeladen", before + d, total)), ct).ConfigureAwait(false);
                    done = before + file.Size;
                    progress?.Report(new MotifModelDownloadProgress("Prüfsumme wird geprüft", done, total));
                    var hash = await ComputeSha256Async(temp, ct).ConfigureAwait(false);
                    if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new MotifModelDownloadException(
                            $"Die Prüfsumme von {file.RelativePath} stimmt nicht (erwartet {file.Sha256}, erhalten {hash}). Die Datei wurde verworfen.");
                    }

                    File.Move(temp, final, overwrite: true);
                }
                finally
                {
                    TryDelete(temp);
                }
            }

            _logger.LogInformation("Motive: Modell heruntergeladen und geprüft ({Folder})", ModelFolder);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Motive: Modell-Download abgebrochen");
            throw;
        }
        catch (MotifModelDownloadException ex)
        {
            _logger.LogWarning("Motive: Modell-Download gescheitert: {Message}", ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Motive: Modell-Download gescheitert");
            throw new MotifModelDownloadException($"Das Modell konnte nicht geladen werden: {ex.Message}", ex);
        }
        finally
        {
            ownClient?.Dispose();
        }

        _messenger.Send(new MotifModelChangedMessage(State));
    }

    private async Task DownloadFileAsync(HttpClient client, MotifModelFile file, string target, Action<long> report, CancellationToken ct)
    {
        var uri = new Uri(DownloadInfo.BaseUri, file.RelativePath);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        var buffer = new byte[BufferSize];
        long done = 0;
        long lastReport = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (done > file.Size)
            {
                throw new MotifModelDownloadException($"{file.RelativePath} ist größer als erwartet ({file.Size:N0} Bytes) — Download abgebrochen.");
            }

            if (done - lastReport >= 1 << 20)
            {
                lastReport = done;
                report(done);
            }
        }

        report(done);
    }

    /// <summary>SHA-256 einer Datei als Kleinbuchstaben-Hex.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
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
