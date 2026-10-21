using System.Collections.Concurrent;

namespace Lumora.Services.Stories;

/// <summary>
/// Zuhause je Gruppe aus dem letzten Erkennungs-Durchgang (ADR-0058) — die Übersicht braucht das Land von zu Hause für
/// „ — Italien“, ohne bei jedem Laden die Orte der ganzen Bibliothek zu zählen. Je Katalog-Sitzung ein Singleton (eine
/// versteckte Bibliothek hat ihren eigenen).
/// </summary>
public sealed class StoryHomeCache
{
    private readonly ConcurrentDictionary<int, StoryHome?> _homes = new();

    public bool TryGet(int groupId, out StoryHome? home) => _homes.TryGetValue(groupId, out home);

    public void Set(int groupId, StoryHome? home) => _homes[groupId] = home;

    public void Clear() => _homes.Clear();
}
