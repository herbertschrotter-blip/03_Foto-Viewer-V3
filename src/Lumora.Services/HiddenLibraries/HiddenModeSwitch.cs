namespace Lumora.Services.HiddenLibraries;

/// <summary>
/// Startschalter für versteckte Bibliotheken (ADR-0049): <c>_hidden</c>, <c>--hidden</c> oder <c>/hidden</c> (Groß/Klein
/// egal). Ohne Schalter zeigt Lumora keinerlei Spur davon. Der Rauchtest (LUMORA_SMOKE_ROOT) simuliert ihn über
/// <see cref="SmokeVariable"/>: „1“ bzw. nicht gesetzt = an (das gemeinsame Rauchtest-Skript setzt die Variable nicht),
/// „0“ = aus.
/// </summary>
public static class HiddenModeSwitch
{
    public const string SmokeVariable = "LUMORA_SMOKE_HIDDEN";

    private static readonly string[] Switches = ["_hidden", "--hidden", "/hidden"];

    public static bool IsRequested(IEnumerable<string> args)
        => args.Any(a => Switches.Any(s => string.Equals(a?.Trim(), s, StringComparison.OrdinalIgnoreCase)));

    /// <param name="smokeRequested">true, wenn ein Rauchtest läuft (nur dann zählt <see cref="SmokeVariable"/>).</param>
    public static bool IsRequested(IEnumerable<string> args, bool smokeRequested, Func<string, string?> environment)
        => IsRequested(args) || (smokeRequested && environment(SmokeVariable) != "0");
}

/// <summary>
/// Untätigkeit in Lumora (ADR-0049): jede Maus-/Tastatureingabe ruft <see cref="NotifyActivity"/>; nach
/// <see cref="Timeout"/> ohne Eingabe ist <see cref="IsExpired"/> true und die versteckte Sitzung wird zugesperrt.
/// Zeit über <see cref="TimeProvider"/> (Tests: eigene Uhr).
/// </summary>
public sealed class IdleLockMonitor
{
    private readonly TimeProvider _time;
    private long _lastActivityTicks;

    public IdleLockMonitor(TimeProvider time, TimeSpan timeout)
    {
        _time = time;
        Timeout = timeout;
        NotifyActivity();
    }

    public TimeSpan Timeout { get; set; }

    public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public void NotifyActivity() => Interlocked.Exchange(ref _lastActivityTicks, _time.GetUtcNow().UtcTicks);

    public TimeSpan Remaining
    {
        get
        {
            var left = Timeout - (_time.GetUtcNow() - LastActivity);
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    public bool IsExpired => Remaining == TimeSpan.Zero;
}
