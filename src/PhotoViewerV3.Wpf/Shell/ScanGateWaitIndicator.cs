using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Anzeige-Modell für Wartezustände der Scan-Sperre (0.2.22, ADR-0025): sammelt die laufenden Wartevorgänge, die der
/// Filter durchlässt (Statusleiste: Datei-Operationen), aus <see cref="ScanGateWaitStartedMessage"/> und
/// <see cref="ScanGateWaitEndedMessage"/> und bietet „Abbrechen“ über <see cref="IScanGate.CancelWait"/> an.
/// Nachrichten kommen von Worker-Threads und werden auf den UI-Thread gestellt.
/// </summary>
public sealed partial class ScanGateWaitIndicator : ObservableObject, IDisposable
{
    private readonly IScanGate _gate;
    private readonly Func<ScanGateWait, bool> _filter;
    private readonly IMessenger _messenger;
    private readonly List<ScanGateWait> _active = new();
    private readonly HashSet<long> _ended = new();

    /// <summary>true, solange mindestens ein passender Vorgang wartet.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isWaiting;

    /// <summary>„Wartet auf laufenden Scan…“ bzw. „Wartet auf laufende Datei-Operation…“ (bei mehreren mit Anzahl); null = nichts.</summary>
    [ObservableProperty] private string? _text;

    /// <summary>Erklärung für den Tooltip.</summary>
    [ObservableProperty] private string? _toolTip;

    public ScanGateWaitIndicator(IScanGate gate, Func<ScanGateWait, bool> filter, IMessenger? messenger = null)
    {
        _gate = gate;
        _filter = filter;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _messenger.Register<ScanGateWaitIndicator, ScanGateWaitStartedMessage>(this, static (r, m) => UiThread.Post(() => r.OnStarted(m.Wait)));
        _messenger.Register<ScanGateWaitIndicator, ScanGateWaitEndedMessage>(this, static (r, m) => UiThread.Post(() => r.OnEnded(m)));

        // Wartende, die vor dem Erzeugen begonnen haben (Momentaufnahme; ihr Ende kommt als Nachricht).
        foreach (var wait in gate.GetWaits())
        {
            OnStarted(wait);
        }
    }

    /// <summary>Wird nach jedem beendeten passenden Warten ausgelöst (UI-Thread), z. B. für „Datei-Operation abgebrochen“.</summary>
    public event Action<ScanGateWaitEndedMessage>? Ended;

    /// <summary>Die gerade wartenden Vorgänge (UI-Thread).</summary>
    public IReadOnlyList<ScanGateWait> ActiveWaits => _active;

    /// <summary>Alle angezeigten Wartevorgänge abbrechen; der Aufrufer der Operation bekommt eine <see cref="ScanGateWaitCanceledException"/>.</summary>
    [RelayCommand(CanExecute = nameof(IsWaiting))]
    private void Cancel()
    {
        foreach (var wait in _active.ToList())
        {
            _gate.CancelWait(wait.WaitId);
        }
    }

    private void OnStarted(ScanGateWait wait)
    {
        if (!_filter(wait) || _ended.Remove(wait.WaitId) || _active.Any(w => w.WaitId == wait.WaitId))
        {
            return; // nicht relevant, schon beendet (Ende vor dem Beginn verarbeitet) oder doppelt (Momentaufnahme + Nachricht)
        }

        _active.Add(wait);
        Update();
    }

    private void OnEnded(ScanGateWaitEndedMessage message)
    {
        if (!_filter(message.Wait))
        {
            return;
        }

        var index = _active.FindIndex(w => w.WaitId == message.Wait.WaitId);
        if (index < 0)
        {
            _ended.Add(message.Wait.WaitId); // Beginn steht noch aus (Momentaufnahme im Konstruktor)
            return;
        }

        _active.RemoveAt(index);
        Update();
        Ended?.Invoke(message);
    }

    private void Update()
    {
        IsWaiting = _active.Count > 0;
        if (!IsWaiting)
        {
            Text = null;
            ToolTip = null;
            return;
        }

        var first = _active[0];
        Text = _active.Count == 1 ? Formatting.GateWait(first.Holder) : $"{Formatting.GateWait(first.Holder)} ({_active.Count} Vorgänge)";
        ToolTip = first.Waiter == ScanGateUse.FileOperation
            ? "Die Datei-Operation startet, sobald die Bibliothek frei ist. „Abbrechen“ verwirft sie, bevor etwas geändert wurde."
            : "Der Scan startet, sobald die Bibliothek frei ist.";
    }

    public void Dispose() => _messenger.UnregisterAll(this);
}
