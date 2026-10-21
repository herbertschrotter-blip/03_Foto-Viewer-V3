using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Watch;

/// <summary>
/// Aufrufer-Kennung der Scan-Sperre (0.2.29, offener Punkt aus 0.2.22): die Kennung aus
/// <see cref="IScanGate.AcquireAsync(int, Guid?, CancellationToken)"/> bzw. <see cref="ScanOptions.GateCallerId"/> steht in
/// <see cref="ScanGateWait.CallerId"/> — in den Wartenachrichten und in <see cref="IScanGate.GetWaits"/>.
/// </summary>
public sealed class ScanGateCallerIdTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(what);
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Concurrent_waits_of_one_library_carry_their_own_caller_ids()
    {
        var messenger = new WeakReferenceMessenger();
        var started = new ConcurrentQueue<ScanGateWait>();
        var ended = new ConcurrentQueue<ScanGateWaitEndedMessage>();
        messenger.Register<ScanGateWaitStartedMessage>(started, (_, m) => started.Enqueue(m.Wait));
        messenger.Register<ScanGateWaitEndedMessage>(ended, (_, m) => ended.Enqueue(m));
        var gate = new ScanGate(messenger);
        var watch = Guid.NewGuid();
        var page = Guid.NewGuid();

        var batch = await gate.AcquireForFileOperationAsync(1, CancellationToken.None);
        var watchScan = gate.AcquireAsync(1, watch, CancellationToken.None);
        var pageScan = gate.AcquireAsync(1, page, CancellationToken.None);
        var anonymousScan = gate.AcquireAsync(1, CancellationToken.None);
        var secondBatch = gate.AcquireForFileOperationAsync(1, CancellationToken.None);
        await WaitUntilAsync(() => gate.GetWaits().Count == 4, "vier Wartende");

        gate.GetWaits().Select(w => w.CallerId).Should().Equal(watch, page, null, null);
        started.Select(w => (w.Waiter, w.CallerId)).Should().Equal(
            (ScanGateUse.Scan, watch), (ScanGateUse.Scan, page), (ScanGateUse.Scan, (Guid?)null), (ScanGateUse.FileOperation, (Guid?)null));

        batch.Dispose();
        (await watchScan.WaitAsync(Timeout)).Dispose();
        (await pageScan.WaitAsync(Timeout)).Dispose();
        (await anonymousScan.WaitAsync(Timeout)).Dispose();
        (await secondBatch.WaitAsync(Timeout)).Dispose();
        await WaitUntilAsync(() => ended.Count == 4, "vier Enden");
        ended.Select(m => m.Wait.CallerId).Should().Equal(watch, page, null, null);
        ended.Should().OnlyContain(m => m.Acquired);
    }

    [Fact]
    public async Task ScanService_passes_the_caller_id_from_the_scan_options_to_the_gate()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        var started = new ConcurrentQueue<ScanGateWait>();
        fx.Messenger.Register<ScanGateWaitStartedMessage>(started, (_, m) => started.Enqueue(m.Wait));
        var gate = new ScanGate(fx.Messenger);
        var scans = fx.CreateScanService(gate);
        var caller = Guid.NewGuid();

        var batch = await gate.AcquireForFileOperationAsync(library.Id, CancellationToken.None);
        var scan = scans.ScanAsync(library.Id, ScanMode.Fresh, new ScanOptions { GateCallerId = caller }, null, CancellationToken.None);
        await WaitUntilAsync(() => !started.IsEmpty, "Scan wartet");
        batch.Dispose();
        var result = await scan.WaitAsync(Timeout);

        started.Should().ContainSingle().Which.CallerId.Should().Be(caller);
        result.FilesInserted.Should().Be(1);
    }
}
