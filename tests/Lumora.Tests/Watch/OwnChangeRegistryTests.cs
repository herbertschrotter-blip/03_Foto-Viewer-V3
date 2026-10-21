using Lumora.Services.Watch;

namespace Lumora.Tests.Watch;

/// <summary>
/// Kurzlebige Liste eigener Pfade (0.2.17): eigen ab <c>Add</c>, solange der Scope offen ist, und danach noch die
/// Nachlaufzeit; <c>Revoke</c> nimmt sofort zurück und meldet den Pfad.
/// </summary>
public sealed class OwnChangeRegistryTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "own");

    [Fact]
    public void Path_is_own_while_the_scope_is_open_and_for_the_grace_period_afterwards()
    {
        var time = new ManualTime();
        var registry = new OwnChangeRegistry(TimeSpan.FromSeconds(10), time);
        var path = Path.Combine(Root, "a", "IMG_0001.jpg");

        registry.IsOwn(path).Should().BeFalse();
        var scope = registry.Begin();
        scope.Add(path);
        registry.IsOwn(path).Should().BeTrue();
        registry.IsOwn(path.ToUpperInvariant()).Should().BeTrue("Windows-Pfade ohne Groß-/Kleinschreibung");
        registry.IsOwn(Path.Combine(Root, "a", "IMG_0002.jpg")).Should().BeFalse("nur eingetragene Pfade");

        time.Now += TimeSpan.FromMinutes(5);
        registry.IsOwn(path).Should().BeTrue("ein offener Scope verfällt nicht");

        scope.Dispose();
        time.Now += TimeSpan.FromSeconds(9);
        registry.IsOwn(path).Should().BeTrue("Ereignisse des FileSystemWatcher kommen verzögert");
        time.Now += TimeSpan.FromSeconds(2);
        registry.IsOwn(path).Should().BeFalse("nach der Nachlaufzeit zählen Änderungen wieder");
    }

    [Fact]
    public void Directory_paths_match_with_and_without_trailing_separator()
    {
        var registry = new OwnChangeRegistry();
        var dir = Path.Combine(Root, "2024", "2024-07");
        using var scope = registry.Begin();

        scope.Add(dir + Path.DirectorySeparatorChar);

        registry.IsOwn(dir).Should().BeTrue();
    }

    [Fact]
    public void Revoke_removes_the_path_at_once_and_reports_it()
    {
        var registry = new OwnChangeRegistry();
        var revoked = new List<string>();
        registry.Revoked += revoked.Add;
        var path = Path.Combine(Root, "b", "IMG_0003.jpg");
        using var scope = registry.Begin();
        scope.Add(path);

        scope.Revoke(path);

        registry.IsOwn(path).Should().BeFalse("der Katalog ist für den Pfad nicht gebucht");
        revoked.Should().ContainSingle().Which.Should().BeEquivalentTo(path);
    }

    [Fact]
    public void Path_stays_own_while_another_scope_still_holds_it()
    {
        var time = new ManualTime();
        var registry = new OwnChangeRegistry(TimeSpan.FromSeconds(1), time);
        var path = Path.Combine(Root, "c", "IMG_0004.jpg");
        var first = registry.Begin();
        var second = registry.Begin();
        first.Add(path);
        second.Add(path);

        first.Dispose();
        time.Now += TimeSpan.FromSeconds(5);
        registry.IsOwn(path).Should().BeTrue("der zweite Scope ist noch offen");

        second.Dispose();
        time.Now += TimeSpan.FromSeconds(5);
        registry.IsOwn(path).Should().BeFalse();
    }

    [Fact]
    public void Expired_entries_are_pruned_and_invalid_paths_are_never_own()
    {
        var time = new ManualTime();
        var registry = new OwnChangeRegistry(TimeSpan.FromSeconds(1), time);
        using (var scope = registry.Begin())
        {
            scope.Add(Path.Combine(Root, "d", "IMG_0005.jpg"));
            scope.Add(string.Empty);
            scope.Add("   ");
        }

        time.Now += TimeSpan.FromSeconds(2);
        using (registry.Begin())
        {
            registry.IsOwn(Path.Combine(Root, "d", "IMG_0005.jpg")).Should().BeFalse();
        }

        registry.IsOwn(string.Empty).Should().BeFalse();
        registry.IsOwn("   ").Should().BeFalse();
    }

    [Fact]
    public void AddTree_covers_the_folder_and_everything_below_for_the_grace_period()
    {
        var time = new ManualTime();
        var registry = new OwnChangeRegistry(TimeSpan.FromSeconds(10), time);
        var folder = Path.Combine(Root, "Eingang", "urlaub");
        var scope = registry.Begin();

        scope.AddTree(folder + Path.DirectorySeparatorChar);

        registry.IsOwn(folder).Should().BeTrue("der Ordner selbst (Ereignis „Ordner angelegt“)");
        registry.IsOwn(Path.Combine(folder, "sub", "IMG_0001.jpg")).Should().BeTrue("alles darunter, Pfade stehen vorher nicht fest");
        registry.IsOwn(Path.Combine(Root, "Eingang", "urlaub-2", "IMG_0001.jpg")).Should().BeFalse("nur echte Unterpfade, kein Namenspräfix");
        registry.IsOwn(Path.Combine(Root, "Eingang", "IMG_0001.jpg")).Should().BeFalse("der Ordner darüber gehört nicht dazu");

        scope.Dispose();
        time.Now += TimeSpan.FromSeconds(9);
        registry.IsOwn(Path.Combine(folder, "IMG_0002.jpg")).Should().BeTrue("Nachlaufzeit wie bei einzelnen Pfaden");
        time.Now += TimeSpan.FromSeconds(2);
        registry.IsOwn(Path.Combine(folder, "IMG_0002.jpg")).Should().BeFalse();
        registry.IsOwn(folder).Should().BeFalse();
    }
}
