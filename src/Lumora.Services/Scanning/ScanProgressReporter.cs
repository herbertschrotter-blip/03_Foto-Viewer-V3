using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Messaging;

namespace Lumora.Services.Scanning;

/// <summary>
/// Drosselt Fortschrittsmeldungen (höchstens alle 250 ms oder alle 2000 Dateien, ADR-0013) und verteilt sie
/// an das optionale IProgress und als <see cref="ScanProgressMessage"/> an den Messenger.
/// Gemeinsam für Fresh/ClearAndRescan und Incremental.
/// </summary>
internal sealed class ScanProgressReporter
{
    public const int FileInterval = 2_000;
    public static readonly TimeSpan TimeInterval = TimeSpan.FromMilliseconds(250);

    private readonly int _libraryId;
    private readonly IProgress<ScanProgress>? _progress;
    private readonly IMessenger _messenger;
    private readonly Stopwatch _sinceLast = Stopwatch.StartNew();
    private int _filesAtLast = -FileInterval; // erste Meldung sofort

    public ScanProgressReporter(int libraryId, IProgress<ScanProgress>? progress, IMessenger messenger)
    {
        _libraryId = libraryId;
        _progress = progress;
        _messenger = messenger;
    }

    public void Report(ScanProgress p, bool force = false)
    {
        if (!force
            && p.FilesDiscovered - _filesAtLast < FileInterval
            && _sinceLast.Elapsed < TimeInterval)
        {
            return;
        }

        _filesAtLast = p.FilesDiscovered;
        _sinceLast.Restart();
        _progress?.Report(p);
        _messenger.Send(new ScanProgressMessage(_libraryId, p));
    }
}
