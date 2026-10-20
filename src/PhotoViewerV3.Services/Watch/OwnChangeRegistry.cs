namespace PhotoViewerV3.Services.Watch;

/// <summary>
/// Kurzlebige Liste der Pfade, die die App gerade selbst schreibt (Datei-Batches des <c>FileOperationService</c>,
/// Ordner-Aufräumen des Sortierers). Die Ordnerüberwachung verwirft Ereignisse, deren Pfade hier als eigen gelten — der
/// Katalog ist für diese Pfade bereits gebucht, ein automatischer Scan fände nichts (Befund aus 0.2.14).
/// <para>
/// Ein Pfad gilt ab <see cref="IOwnChangeScope.Add"/> als eigen, solange sein Scope offen ist, und danach noch
/// <see cref="OwnChangeRegistry.DefaultGracePeriod"/> lang (die Ereignisse des FileSystemWatcher kommen verzögert).
/// Gelingt die Katalog-Buchung nicht, nimmt der Aufrufer den Pfad mit <see cref="IOwnChangeScope.Revoke"/> zurück:
/// <see cref="Revoked"/> meldet ihn der Ordnerüberwachung, die ihn dann wie ein Dateisystem-Ereignis zählt — auch wenn
/// sie das echte Ereignis schon verworfen hat. Externe Änderungen an anderen Pfaden bleiben unberührt; nur eine fremde
/// Änderung genau an einem eigenen Pfad innerhalb der Nachlaufzeit geht in der Zählung unter.
/// </para>
/// </summary>
public interface IOwnChangeRegistry
{
    /// <summary>Wird ausgelöst, wenn ein Pfad zurückgenommen wurde (auf dem Thread des Aufrufers, außerhalb von Sperren).</summary>
    event Action<string>? Revoked;

    /// <summary>Beginnt eine Operation; Dispose startet die Nachlaufzeit ihrer Pfade.</summary>
    IOwnChangeScope Begin();

    /// <summary>true, wenn die App den Pfad gerade schreibt oder vor höchstens der Nachlaufzeit geschrieben hat.</summary>
    bool IsOwn(string path);
}

/// <summary>Pfade einer laufenden Operation (siehe <see cref="IOwnChangeRegistry"/>).</summary>
public interface IOwnChangeScope : IDisposable
{
    /// <summary>Vor dem Dateisystem-Schritt: Datei- oder Ordnerpfad, den die Operation anlegt, löscht oder umbenennt.</summary>
    void Add(string path);

    /// <summary>
    /// Wie <see cref="Add"/>, aber für einen ganzen Ordnerbaum: der Ordner und alles darunter gilt als eigen. Für Schritte,
    /// deren einzelne Pfade vorher nicht feststehen (automatisches Entpacken, ADR-0032). Nicht mit <see cref="Revoke"/>
    /// rücknehmbar — wer einen Baum anmeldet, scannt danach selbst.
    /// </summary>
    void AddTree(string folder);

    /// <summary>
    /// Der Katalog ist für den Pfad nicht gebucht (Dateisystem- oder Katalog-Schritt gescheitert): er gilt nicht mehr als
    /// eigen, und <see cref="IOwnChangeRegistry.Revoked"/> meldet ihn der Ordnerüberwachung.
    /// </summary>
    void Revoke(string path);
}

/// <summary>Thread-sichere Implementierung (Singleton); Pfadvergleich ohne Groß-/Kleinschreibung, abgelaufene Einträge werden beim nächsten <see cref="Begin"/> entfernt.</summary>
public sealed class OwnChangeRegistry : IOwnChangeRegistry
{
    /// <summary>Nachlaufzeit nach dem Ende einer Operation.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _trees = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _gracePeriod;
    private readonly TimeProvider _time;

    /// <param name="gracePeriod">Optional (Tests); produktiv <see cref="DefaultGracePeriod"/>.</param>
    /// <param name="time">Optional (Tests); produktiv <see cref="TimeProvider.System"/>.</param>
    public OwnChangeRegistry(TimeSpan? gracePeriod = null, TimeProvider? time = null)
    {
        _gracePeriod = gracePeriod ?? DefaultGracePeriod;
        _time = time ?? TimeProvider.System;
    }

    public event Action<string>? Revoked;

    public IOwnChangeScope Begin()
    {
        lock (_gate)
        {
            PruneLocked();
        }

        return new Scope(this);
    }

    public bool IsOwn(string path)
    {
        var key = Normalize(path);
        if (key is null)
        {
            return false;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (IsLiveLocked(_entries, key, now))
            {
                return true;
            }

            if (_trees.Count == 0)
            {
                return false;
            }

            // Ordnerbäume (AddTree): der Pfad selbst oder einer seiner übergeordneten Ordner
            for (var folder = key; !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                if (IsLiveLocked(_trees, folder, now))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>true, wenn der Eintrag aktiv oder in der Nachlaufzeit ist; abgelaufene Einträge werden entfernt.</summary>
    private static bool IsLiveLocked(Dictionary<string, Entry> entries, string key, DateTimeOffset now)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.Active > 0 || now < entry.ExpiresAt)
        {
            return true;
        }

        entries.Remove(key);
        return false;
    }

    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static void AddLocked(Dictionary<string, Entry> entries, string key)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            entries[key] = entry;
        }

        entry.Active++;
    }

    /// <summary>Scope beendet (oder Pfad zurückgenommen): ab jetzt läuft die Nachlaufzeit bzw. der Eintrag verfällt sofort.</summary>
    private void ReleaseLocked(Dictionary<string, Entry> entries, string key, int count, bool revoke)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            return;
        }

        entry.Active = Math.Max(0, entry.Active - count);
        if (entry.Active > 0)
        {
            return;
        }

        if (revoke)
        {
            entries.Remove(key);
        }
        else
        {
            entry.ExpiresAt = _time.GetUtcNow() + _gracePeriod;
        }
    }

    private void PruneLocked()
    {
        var now = _time.GetUtcNow();
        foreach (var entries in new[] { _entries, _trees })
        {
            if (entries.Count == 0)
            {
                continue;
            }

            foreach (var key in entries.Where(kv => kv.Value.Active == 0 && kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
            {
                entries.Remove(key);
            }
        }
    }

    private void RaiseRevoked(string path)
    {
        try
        {
            Revoked?.Invoke(path);
        }
        catch
        {
            // Beobachter-Fehler dürfen die Datei-Operation nicht stören (die Ordnerüberwachung protokolliert selbst).
        }
    }

    private sealed class Entry
    {
        public int Active;
        public DateTimeOffset ExpiresAt;
    }

    private sealed class Scope(OwnChangeRegistry owner) : IOwnChangeScope
    {
        private readonly Dictionary<string, int> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _trees = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public void Add(string path)
        {
            var key = Normalize(path);
            if (key is null)
            {
                return;
            }

            lock (owner._gate)
            {
                if (_disposed)
                {
                    return;
                }

                AddLocked(owner._entries, key);
                _paths[key] = _paths.GetValueOrDefault(key) + 1;
            }
        }

        public void AddTree(string folder)
        {
            var key = Normalize(folder);
            if (key is null)
            {
                return;
            }

            lock (owner._gate)
            {
                if (_disposed)
                {
                    return;
                }

                AddLocked(owner._trees, key);
                _trees[key] = _trees.GetValueOrDefault(key) + 1;
            }
        }

        public void Revoke(string path)
        {
            var key = Normalize(path);
            if (key is null)
            {
                return;
            }

            lock (owner._gate)
            {
                if (_paths.Remove(key, out var count))
                {
                    owner.ReleaseLocked(owner._entries, key, count, revoke: true);
                }
            }

            owner.RaiseRevoked(key);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (var (key, count) in _paths)
                {
                    owner.ReleaseLocked(owner._entries, key, count, revoke: false);
                }

                foreach (var (key, count) in _trees)
                {
                    owner.ReleaseLocked(owner._trees, key, count, revoke: false);
                }

                _paths.Clear();
                _trees.Clear();
            }
        }
    }
}
