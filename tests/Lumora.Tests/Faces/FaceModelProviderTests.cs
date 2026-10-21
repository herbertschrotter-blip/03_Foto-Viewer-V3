using System.IO.Compression;
using System.Net;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Faces.Models;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Faces;

/// <summary>
/// Modellverwaltung (ADR-0035) — der echte Download wird NIE ausgeführt: Quelle ist ein lokales Archiv (<c>file://</c>)
/// bzw. ein HTTP-Handler im Speicher. Geprüft: SHA-256, nur die zwei benötigten Dateien, nichts Halbes bei falscher
/// Prüfsumme oder Abbruch, Nachricht danach, Umgebungsvariable, Standardquelle.
/// </summary>
public sealed class FaceModelProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Lumora.Tests", Guid.NewGuid().ToString("N"));

    public FaceModelProviderTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Archiv_mit_richtiger_Pruefsumme_liefert_nur_die_zwei_Modelle()
    {
        var zip = CreateArchive();
        var sha = await FaceModelProvider.ComputeSha256Async(zip, CancellationToken.None);
        var messenger = new WeakReferenceMessenger();
        var changed = new List<FaceModelState>();
        messenger.Register<List<FaceModelState>, FaceModelChangedMessage>(changed, (r, m) => r.Add(m.State));
        var progress = new List<FaceModelDownloadProgress>();
        var provider = CreateProvider(new FaceModelDownloadInfo(new Uri(zip), sha, new FileInfo(zip).Length), messenger);
        provider.State.Should().Be(FaceModelState.Missing);

        await provider.DownloadAsync(new InlineProgress<FaceModelDownloadProgress>(progress.Add), CancellationToken.None);

        provider.State.Should().Be(FaceModelState.Ready);
        File.ReadAllText(provider.DetectorPath).Should().Be("det");
        File.ReadAllText(provider.RecognizerPath).Should().Be("rec");
        Directory.GetFiles(provider.ModelFolder).Select(Path.GetFileName).Should().BeEquivalentTo(["det_10g.onnx", "w600k_r50.onnx"],
            "andere Modelle des Archivs und das Archiv selbst bleiben nicht liegen");
        changed.Should().Equal(FaceModelState.Ready);
        progress.Should().Contain(p => p.Stage.Contains("Prüfsumme"));
    }

    [Fact]
    public async Task Falsche_Pruefsumme_verwirft_das_Archiv_ohne_Modelle()
    {
        var zip = CreateArchive();
        var provider = CreateProvider(new FaceModelDownloadInfo(new Uri(zip), new string('0', 64), 1), new WeakReferenceMessenger());

        var act = () => provider.DownloadAsync(null, CancellationToken.None);

        (await act.Should().ThrowAsync<FaceModelDownloadException>()).WithMessage("*Prüfsumme*");
        provider.State.Should().Be(FaceModelState.Missing);
        Directory.GetFiles(provider.ModelFolder).Should().BeEmpty();
    }

    [Fact]
    public async Task Abbruch_laesst_nichts_liegen()
    {
        var zip = CreateArchive();
        var sha = await FaceModelProvider.ComputeSha256Async(zip, CancellationToken.None);
        var provider = CreateProvider(new FaceModelDownloadInfo(new Uri(zip), sha, 1), new WeakReferenceMessenger());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => provider.DownloadAsync(null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.State.Should().Be(FaceModelState.Missing);
        Directory.GetFiles(provider.ModelFolder).Should().BeEmpty();
    }

    [Fact]
    public async Task Http_Quelle_ueber_lokalen_Handler_mit_Fortschritt_und_HTTP_Fehler()
    {
        var zip = CreateArchive();
        var bytes = await File.ReadAllBytesAsync(zip);
        var sha = await FaceModelProvider.ComputeSha256Async(zip, CancellationToken.None);
        var info = new FaceModelDownloadInfo(new Uri("https://example.invalid/buffalo_l.zip"), sha, bytes.Length);

        using var ok = new HttpClient(new StaticHandler(HttpStatusCode.OK, bytes));
        var provider = new FaceModelProvider(new TestAppDataPaths(_dir), messenger: new WeakReferenceMessenger(), downloadInfo: info,
            modelFolder: Path.Combine(_dir, "http-ok"), http: ok);
        await provider.DownloadAsync(null, CancellationToken.None);
        provider.State.Should().Be(FaceModelState.Ready);

        using var notFound = new HttpClient(new StaticHandler(HttpStatusCode.NotFound, []));
        var failing = new FaceModelProvider(new TestAppDataPaths(_dir), messenger: new WeakReferenceMessenger(), downloadInfo: info,
            modelFolder: Path.Combine(_dir, "http-404"), http: notFound);
        var act = () => failing.DownloadAsync(null, CancellationToken.None);
        await act.Should().ThrowAsync<FaceModelDownloadException>();
        Directory.GetFiles(failing.ModelFolder).Should().BeEmpty();
    }

    [Fact]
    public void Standardquelle_Pruefsumme_und_Ordner_unter_AppData()
    {
        var info = FaceModelDownloadInfo.BuffaloL;
        info.Source.AbsoluteUri.Should().Be("https://github.com/deepinsight/insightface/releases/download/v0.7/buffalo_l.zip");
        info.Sha256.Should().Be("80ffe37d8a5940d59a7384c201a2a38d4741f2f3c51eef46ebb28218a7b0ca2f");
        info.Size.Should().Be(288_621_354);

        // Umgebungsvariable nur lesen (nicht setzen — andere Tests laufen parallel im selben Prozess).
        var overridden = Environment.GetEnvironmentVariable(IFaceModelProvider.OverrideVariable);
        var provider = new FaceModelProvider(new TestAppDataPaths(_dir));
        if (string.IsNullOrWhiteSpace(overridden))
        {
            provider.ModelFolder.Should().Be(Path.Combine(_dir, "models", "buffalo_l"));
            provider.IsOverridden.Should().BeFalse();
        }
        else
        {
            provider.ModelFolder.Should().Be(Path.GetFullPath(overridden));
            provider.IsOverridden.Should().BeTrue();
        }
    }

    private FaceModelProvider CreateProvider(FaceModelDownloadInfo info, IMessenger messenger)
        => new(new TestAppDataPaths(_dir), messenger: messenger, downloadInfo: info, modelFolder: Path.Combine(_dir, "models-" + Guid.NewGuid().ToString("N")));

    private string CreateArchive()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "1k3d68.onnx", "andere");
        Add(zip, "det_10g.onnx", "det");
        Add(zip, "genderage.onnx", "andere");
        Add(zip, "w600k_r50.onnx", "rec");
        return path;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class StaticHandler(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }
}
