using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Watch;

/// <summary>
/// Wartezustände der Scan-Sperre (0.2.22, ADR-0025): wer auf eine belegte Bibliothek warten muss, meldet Beginn und Ende
/// über den Messenger, ist in <see cref="IScanGate.GetWaits"/> sichtbar und lässt sich über
/// <see cref="IScanGate.CancelWait"/> abbrechen. Wer die Sperre sofort bekommt, meldet nichts.
/// </summary>
public sealed class ScanGateWaitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Sammelt die Wartenachrichten eines eigenen Messengers (nie der globale: Tests laufen parallel).</summary>
    private sealed class WaitLog
    {
        public WaitLog(IMessenger messenger)
        {
            messenger.Register<ScanGateWaitStartedMessage>(this, (_, m) => Started.Enqueue(m.Wait));
            messenger.Register<ScanGateWaitEndedMessage>(this, (_, m) => Ended.Enqueue(m));
        }

        public ConcurrentQueue<ScanGateWait> Started { get; } = new();

        public ConcurrentQueue<ScanGateWaitEndedMessage> Ended { get; } = new();
    }

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
    public async Task Free_library_is_acquired_without_any_wait_message()
    {
        var messenger = new WeakReferenceMessenger();
        var log = new WaitLog(messenger);
        var gate = new ScanGate(messenger);

        using (await gate.AcquireAsync(1, CancellationToken.None))
        {
        }

        using (await gate.AcquireForFileOperationAsync(1, CancellationToken.None))
        {
        }

        log.Started.Should().BeEmpty();
        log.Ended.Should().BeEmpty();
        gate.GetWaits().Should().BeEmpty();
    }

    [Fact]
    public async Task File_operation_waiting_for_a_scan_reports_start_and_end()
    {
        var messenger = new WeakReferenceMessenger();
        var log = new WaitLog(messenger);
        var gate = new ScanGate(messenger);

        var scan = await gate.AcquireAsync(7, CancellationToken.None);
        var batch = gate.AcquireForFileOperationAsync(7, CancellationToken.None);

        await WaitUntilAsync(() => !log.Started.IsEmpty, "Wartebeginn gemeldet");
        var wait = log.Started.Single();
        wait.LibraryId.Should().Be(7);
        wait.Waiter.Should().Be(ScanGateUse.FileOperation);
        wait.Holder.Should().Be(ScanGateUse.Scan);
        gate.GetWaits().Should().ContainSingle().Which.Should().Be(wait);
        batch.IsCompleted.Should().BeFalse();
        log.Ended.Should().BeEmpty();

        scan.Dispose();
        using var lease = await batch.WaitAsync(Timeout);

        log.Ended.Should().ContainSingle().Which.Should().Be(new ScanGateWaitEndedMessage(wait, Acquired: true));
        gate.GetWaits().Should().BeEmpty();
        gate.IsBusy(7).Should().BeTrue("der Batch hält jetzt die Sperre");
    }

    [Fact]
    public async Task Scan_waiting_for_a_file_operation_names_the_file_operation_as_holder()
    {
        var messenger = new WeakReferenceMessenger();
        var log = new WaitLog(messenger);
        var gate = new ScanGate(messenger);
        var acquired = 0;
        gate.Acquired += _ => Interlocked.Increment(ref acquired);

        var batch = await gate.AcquireForFileOperationAsync(3, CancellationToken.None);
        var scan = gate.AcquireAsync(3, CancellationToken.None);
        await WaitUntilAsync(() => !log.Started.IsEmpty, "Wartebeginn gemeldet");

        log.Started.Single().Should().Match<ScanGateWait>(w => w.Waiter == ScanGateUse.Scan && w.Holder == ScanGateUse.FileOperation);
        Volatile.Read(ref acquired).Should().Be(0, "der Scan hat die Sperre noch nicht");

        batch.Dispose();
        (await scan.WaitAsync(Timeout)).Dispose();
        Volatile.Read(ref acquired).Should().Be(1);
        log.Ended.Single().Acquired.Should().BeTrue();
    }

    [Fact]
    public async Task CancelWait_aborts_only_the_waiting_caller_with_a_dedicated_exception()
    {
        var messenger = new WeakReferenceMessenger();
        var log = new WaitLog(messenger);
        var gate = new ScanGate(messenger);

        var scan = await gate.AcquireAsync(5, CancellationToken.None);
        var batch = gate.AcquireForFileOperationAsync(5, CancellationToken.None);
        await WaitUntilAsync(() => !log.Started.IsEmpty, "Wartebeginn gemeldet");
        var wait = log.Started.Single();

        gate.CancelWait(wait.WaitId).Should().BeTrue();

        var act = () => batch.WaitAsync(Timeout);
        var thrown = await act.Should().ThrowAsync<ScanGateWaitCanceledException>();
        thrown.Which.Should().BeAssignableTo<OperationCanceledException>("bestehende Abbruch-Pfade der Aufrufer greifen");
        thrown.Which.Wait.Should().Be(wait);
        thrown.Which.Message.Should().Contain("laufenden Scan");
        await WaitUntilAsync(() => !log.Ended.IsEmpty, "Warteende gemeldet");
        log.Ended.Single().Should().Be(new ScanGateWaitEndedMessage(wait, Acquired: false, CanceledByUser: true));
        gate.GetWaits().Should().BeEmpty();
        gate.CancelWait(wait.WaitId).Should().BeFalse("das Warten ist vorbei");
        gate.IsBusy(5).Should().BeTrue("der Scan hält die Sperre weiter");

        scan.Dispose();
        gate.IsBusy(5).Should().BeFalse("der abgebrochene Wartende hat nichts belegt");
        using var next = await gate.AcquireForFileOperationAsync(5, CancellationToken.None).WaitAsync(Timeout);
    }

    [Fact]
    public async Task Caller_token_cancels_the_wait_without_counting_as_user_cancel()
    {
        var messenger = new WeakReferenceMessenger();
        var log = new WaitLog(messenger);
        var gate = new ScanGate(messenger);
        using var cts = new CancellationTokenSource();

        using var scan = await gate.AcquireAsync(2, CancellationToken.None);
        var second = gate.AcquireAsync(2, cts.Token);
        await WaitUntilAsync(() => !log.Started.IsEmpty, "Wartebeginn gemeldet");

        await cts.CancelAsync();
        var act = () => second.WaitAsync(Timeout);
        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Should().NotBeOfType<ScanGateWaitCanceledException>();
        await WaitUntilAsync(() => !log.Ended.IsEmpty, "Warteende gemeldet");
        log.Ended.Single().Should().Match<ScanGateWaitEndedMessage>(m => !m.Acquired && !m.CanceledByUser);
    }

    [Fact]
    public void CancelWait_with_unknown_id_returns_false()
    {
        new ScanGate(new WeakReferenceMessenger()).CancelWait(42).Should().BeFalse();
    }

    [Fact]
    public async Task Waits_of_different_libraries_are_independent_and_listed_in_order()
    {
        var messenger = new WeakReferenceMessenger();
        var log = new WaitLog(messenger);
        var gate = new ScanGate(messenger);

        var scanA = await gate.AcquireAsync(1, CancellationToken.None);
        var scanB = await gate.AcquireAsync(2, CancellationToken.None);
        var waitA = gate.AcquireForFileOperationAsync(1, CancellationToken.None);
        await WaitUntilAsync(() => log.Started.Count == 1, "erstes Warten");
        var waitB = gate.AcquireForFileOperationAsync(2, CancellationToken.None);
        await WaitUntilAsync(() => log.Started.Count == 2, "zweites Warten");

        gate.GetWaits().Select(w => w.LibraryId).Should().Equal(1, 2);

        scanB.Dispose();
        (await waitB.WaitAsync(Timeout)).Dispose();
        gate.GetWaits().Should().ContainSingle().Which.LibraryId.Should().Be(1);

        scanA.Dispose();
        (await waitA.WaitAsync(Timeout)).Dispose();
        gate.GetWaits().Should().BeEmpty();
    }

    [Fact]
    public async Task Move_waiting_for_a_scan_can_be_canceled_before_anything_is_logged_or_moved()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        var source = fx.WriteLibraryFile(Path.Combine("a", "IMG_0001.jpg"));
        (await fx.ScanAsync(library.Id)).FilesInserted.Should().Be(1);
        var fileId = (await fx.FindFileAsync("IMG_0001.jpg"))!.Id;

        var messenger = new WeakReferenceMessenger();
        var log = new WaitLog(messenger);
        var gate = new ScanGate(messenger);
        var fileOps = fx.CreateFileOperationService(scanGate: gate);
        var target = Path.Combine(fx.LibraryRoot, "b");

        using var scan = await gate.AcquireAsync(library.Id, CancellationToken.None); // „laufender Scan“
        var move = fileOps.MoveAsync([fileId], target);
        await WaitUntilAsync(() => !log.Started.IsEmpty, "Datei-Operation meldet das Warten");
        var wait = log.Started.Single();
        wait.Should().Match<ScanGateWait>(w => w.LibraryId == library.Id && w.Waiter == ScanGateUse.FileOperation && w.Holder == ScanGateUse.Scan);

        gate.CancelWait(wait.WaitId).Should().BeTrue();
        var act = () => move.WaitAsync(Timeout);
        await act.Should().ThrowAsync<ScanGateWaitCanceledException>();

        File.Exists(source).Should().BeTrue("nichts verschoben");
        Directory.Exists(target).Should().BeFalse();
        await using (var ctx = fx.CreateContext())
        {
            (await ctx.OperationBatches.CountAsync()).Should().Be(0, "der Batch wurde nie protokolliert");
        }

        // Nach dem Abbruch läuft die nächste Operation normal, sobald der Scan fertig ist.
        scan.Dispose();
        var result = await fileOps.MoveAsync([fileId], target).WaitAsync(Timeout);
        result.Succeeded.Should().Be(1);
    }
}
