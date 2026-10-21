using Lumora.Domain.Enums;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Stories;

/// <summary>
/// Storys über „Neu aufbauen“ und Datei-Operationen (ADR-0058): Benutzer-Zustand (angeheftet, ausgeblendet, Titel,
/// Titelbild, manuelle Zu-/Abgänge) bleibt über neue FileIds erhalten; eine entfernte Datei verschwindet aus den Storys,
/// „Rückgängig“ bringt manuelle Einträge zurück (FileLinkUndo, ADR-0054).
/// </summary>
public sealed class StoryCarryOverTests
{
    [Fact]
    public async Task Neu_aufbauen_behaelt_Benutzer_Zustand_auf_den_neuen_Ids()
    {
        using var w = await StoryWorld.CreateAsync();
        await WriteAndScanAsync(w);
        var group = await w.GroupIdAsync();
        await w.Index.RunFullAsync(group);
        var story = w.All().Single(s => s.Kind == StoryKind.Event).Id;
        var u01 = await IdAsync(w, "u01.jpg");
        var u05 = await IdAsync(w, "u05.jpg");
        var x1 = await IdAsync(w, "x1.jpg");
        await w.Stories.SetPinnedAsync(story, true);
        await w.Stories.SetHiddenAsync(story, true);
        await w.Stories.SetCustomTitleAsync(story, "Baden");
        await w.Stories.SetCoverAsync(story, u05);
        await w.Stories.AddFilesAsync(story, [x1]);
        await w.Stories.RemoveFilesAsync(story, [u01]);

        await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);

        var newU01 = await IdAsync(w, "u01.jpg");
        var newU05 = await IdAsync(w, "u05.jpg");
        var newX1 = await IdAsync(w, "x1.jpg");
        newU05.Should().NotBe(u05, "„Neu aufbauen“ vergibt neue FileIds");
        AssertState();

        await w.Index.RunFullAsync(group);
        w.All().Single(s => s.Kind == StoryKind.Event).Id.Should().Be(story, "die Erkennung findet die Story über die Signatur wieder");
        AssertState();

        void AssertState()
        {
            var card = w.Stories.GetAsync(story).GetAwaiter().GetResult()!;
            card.IsPinned.Should().BeTrue();
            card.IsHidden.Should().BeTrue();
            card.Title.Should().Be("Baden");
            card.CoverFileId.Should().Be(newU05);
            card.CoverIsManual.Should().BeTrue();
            card.FileCount.Should().Be(14, "13 erkannte + 1 hinzugefügte, der Ausschluss zählt nicht");
            w.Items(story).Should().Contain(newX1).And.NotContain(newU01);
            w.Scalar("SELECT Origin FROM StoryItems WHERE StoryId = $s AND FileId = $f", ("$s", story), ("$f", newU01)).Should().Be(-1);
        }
    }

    [Fact]
    public async Task Entfernte_Datei_verschwindet_aus_Storys_Rueckgaengig_bringt_manuelle_Eintraege_zurueck()
    {
        using var w = await StoryWorld.CreateAsync();
        await WriteAndScanAsync(w);
        var group = await w.GroupIdAsync();
        await w.Index.RunFullAsync(group);
        var story = w.All().Single(s => s.Kind == StoryKind.Event).Id;
        var u03 = await IdAsync(w, "u03.jpg");
        var x1 = await IdAsync(w, "x1.jpg");
        await w.Stories.AddFilesAsync(story, [x1]);
        using var ops = w.CreateFileOperations();

        // Automatischer Eintrag: Datei aus der Bibliothek heraus → Eintrag per Kaskade weg, Zähler nach der Erkennung
        (await ops.MoveAsync([u03], w.Fx.OutsideDir)).Succeeded.Should().Be(1);
        w.Items(story).Should().NotContain(u03);
        await w.Index.RunFullAsync(group);
        (await w.Stories.GetAsync(story))!.FileCount.Should().Be(14);

        // Manueller Eintrag: zur Operation gesichert, „Rückgängig“ stellt ihn auf der neuen Files-Zeile wieder her
        (await ops.MoveAsync([x1], w.Fx.OutsideDir)).Succeeded.Should().Be(1);
        w.Scalar("SELECT COUNT(*) FROM FileLinkUndo WHERE Kind = 2 AND TargetId = $s", ("$s", story)).Should().Be(1);
        (await ops.UndoLastAsync())!.Succeeded.Should().Be(1);
        var back = await IdAsync(w, "x1.jpg");
        w.Scalar("SELECT Origin FROM StoryItems WHERE StoryId = $s AND FileId = $f", ("$s", story), ("$f", back)).Should().Be(1);
        w.Scalar("SELECT COUNT(*) FROM FileLinkUndo").Should().Be(0);
        (await w.Stories.GetAsync(story))!.FileCount.Should().Be(14);
    }

    /// <summary>14 Fotos am 12.07.2025 ab 9:00 (u01–u14) und zwei im März (x1, x2) — Aufnahmedatum „manuell“, damit es „Neu aufbauen“ übersteht.</summary>
    private static async Task WriteAndScanAsync(StoryWorld w)
    {
        for (var i = 1; i <= 14; i++)
        {
            w.Fx.WriteLibraryFile(Path.Combine("Baden", $"u{i:D2}.jpg"), 2000 + i);
        }

        w.Fx.WriteLibraryFile(Path.Combine("Sonst", "x1.jpg"), 3001);
        w.Fx.WriteLibraryFile(Path.Combine("Sonst", "x2.jpg"), 3002);
        await w.Fx.ScanAsync(w.LibraryId);
        for (var i = 1; i <= 14; i++)
        {
            SetDate(w, $"u{i:D2}.jpg", StoryWorld.Utc(2025, 7, 12, 9).AddMinutes(5 * i));
        }

        SetDate(w, "x1.jpg", StoryWorld.Utc(2025, 3, 1, 9));
        SetDate(w, "x2.jpg", StoryWorld.Utc(2025, 3, 1, 10));
    }

    private static void SetDate(StoryWorld w, string name, DateTime utc)
        => w.Execute("UPDATE Files SET CapturedAt = $t, CapturedAtSource = 4, CapturedAtPrecision = 0 WHERE Name = $n",
            ("$t", new DateTimeOffset(utc).ToUnixTimeSeconds()), ("$n", name));

    private static async Task<int> IdAsync(StoryWorld w, string name) => (await w.Fx.FindFileAsync(name))!.Id;
}
