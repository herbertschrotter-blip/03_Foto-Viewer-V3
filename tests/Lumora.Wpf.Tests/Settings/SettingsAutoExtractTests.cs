using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Archive;
using Lumora.Services.Library;
using Lumora.Settings;
using Lumora.Shell;

namespace Lumora.Wpf.Tests.Settings;

/// <summary>
/// Einstellungen → „Archive“: Schalter „Neue Archive in Eingangsordnern automatisch entpacken“ und „Papierkorb danach“
/// für die aktive Bibliothek (ADR-0032/0033), „Vorhandene Archive in Eingängen jetzt entpacken…“ sowie der
/// Statusleisten-Text des automatischen Entpackens.
/// </summary>
public sealed class SettingsAutoExtractTests
{
    private sealed class FakeAutoExtract : IArchiveAutoExtractService
    {
        public Dictionary<int, ArchiveAutoExtractSettings> Settings { get; } = new();

        public ArchiveAutoExtractSummary? LastSummary => null;

        public List<(int LibraryId, bool Value)> EnabledCalls { get; } = [];

        public List<(int LibraryId, bool Value)> RecycleCalls { get; } = [];

        public List<int> ExtractExistingCalls { get; } = [];

        public int ExistingCount { get; set; }

        public Exception? Fail { get; set; }

        public ArchiveAutoExtractSettings GetSettings(int libraryId) => Settings.GetValueOrDefault(libraryId, ArchiveAutoExtractSettings.Default);

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetEnabledAsync(int libraryId, bool enabled, CancellationToken ct = default)
        {
            EnabledCalls.Add((libraryId, enabled));
            if (Fail is not null)
            {
                return Task.FromException(Fail);
            }

            Settings[libraryId] = GetSettings(libraryId) with { Enabled = enabled };
            return Task.CompletedTask;
        }

        public Task SetRecycleAfterExtractAsync(int libraryId, bool recycle, CancellationToken ct = default)
        {
            RecycleCalls.Add((libraryId, recycle));
            if (Fail is not null)
            {
                return Task.FromException(Fail);
            }

            Settings[libraryId] = GetSettings(libraryId) with { RecycleAfterExtract = recycle };
            return Task.CompletedTask;
        }

        public Task<int> CountExistingAsync(int libraryId, CancellationToken ct = default) => Task.FromResult(ExistingCount);

        public Task<int> ExtractExistingAsync(int libraryId, CancellationToken ct = default)
        {
            ExtractExistingCalls.Add(libraryId);
            return Task.FromResult(GetSettings(libraryId).Enabled ? ExistingCount : 0);
        }

        public Task WhenIdleAsync() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class FakeLibraries : ILibraryService
    {
        public LibraryInfo? Active { get; set; }

        public Task<LibraryInfo?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult(Active);

        public Task<IReadOnlyList<LibraryInfo>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LibraryInfo?> GetAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LibraryInfo> CreateAsync(string name, string rootPath, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RenameAsync(int libraryId, string newName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetActiveAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ClearAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteThumbnailCacheAsync(int libraryId) => throw new NotSupportedException();

        public Task DeleteAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryRootInfo>> GetRootsAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LibraryRootInfo> AddRootAsync(int libraryId, string path, LibraryRootRole role, string? name = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RenameRootAsync(int rootId, string newName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetRootRoleAsync(int rootId, LibraryRootRole role, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveRootAsync(int rootId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool ConfirmAnswer { get; set; } = true;

        public List<string> Confirmations { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
        {
            Confirmations.Add(message);
            return Task.FromResult(ConfirmAnswer);
        }

        public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
            => Task.FromResult(DialogChoice.None);

        public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK") => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public string? PickFolder(string title) => null;
    }

    private static LibraryInfo Library(int id, string name)
        => new(id, name, @"C:\Fotos", DateTime.UtcNow, DateTime.UtcNow, 0, 0, true, []);

    private static SettingsViewModel Create(IArchiveAutoExtractService? service, ILibraryService? libraries, IDialogService? dialogs = null)
        // Nur LoadAsync bräuchte die übrigen Dienste — die Archiv-Schalter kommen ohne sie aus.
        => new(null!, null!, null!, null!, null!, null!, dialogs ?? new FakeDialogs(), null!, null!, null!, NullLogger<SettingsViewModel>.Instance, service, libraries);

    [Fact]
    public async Task Laden_zeigt_den_Stand_der_aktiven_Bibliothek_ohne_ihn_neu_zu_speichern()
    {
        var service = new FakeAutoExtract();
        service.Settings[7] = new ArchiveAutoExtractSettings(true, true);
        service.Settings[8] = new ArchiveAutoExtractSettings(false, false);
        var libraries = new FakeLibraries { Active = Library(7, "Fotos") };
        var vm = Create(service, libraries);
        vm.IsAutoExtractEnabled.Should().BeFalse("vor dem Laden gilt der Standard: aus");

        await vm.LoadAutoExtractSettingsAsync();

        vm.IsAutoExtractAvailable.Should().BeTrue();
        vm.AutoExtractLibraryId.Should().Be(7);
        vm.AutoExtractLibraryText.Should().Contain("„Fotos“");
        vm.IsAutoExtractEnabled.Should().BeTrue();
        vm.IsAutoExtractRecycle.Should().BeTrue();
        service.EnabledCalls.Should().BeEmpty();
        service.RecycleCalls.Should().BeEmpty();

        // Andere Bibliothek aktiv → deren Stand (Standard aus)
        libraries.Active = Library(9, "Neu");
        await vm.LoadAutoExtractSettingsAsync();
        vm.AutoExtractLibraryId.Should().Be(9);
        vm.IsAutoExtractEnabled.Should().BeFalse();
        vm.IsAutoExtractRecycle.Should().BeFalse();
        service.EnabledCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Schalter_speichern_sofort_fuer_die_aktive_Bibliothek()
    {
        var service = new FakeAutoExtract();
        var vm = Create(service, new FakeLibraries { Active = Library(7, "Fotos") });
        await vm.LoadAutoExtractSettingsAsync();

        vm.IsAutoExtractEnabled = true;
        vm.IsAutoExtractRecycle = true;
        vm.IsAutoExtractEnabled = false;

        service.EnabledCalls.Should().Equal((7, true), (7, false));
        service.RecycleCalls.Should().Equal((7, true));
        service.GetSettings(7).Should().Be(new ArchiveAutoExtractSettings(false, true));
        service.GetSettings(8).Should().Be(ArchiveAutoExtractSettings.Default, "andere Bibliotheken bleiben unberührt");
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Speicherfehler_meldet_und_setzt_den_Schalter_zurueck()
    {
        var service = new FakeAutoExtract { Fail = new System.IO.IOException("Datenbank gesperrt") };
        var vm = Create(service, new FakeLibraries { Active = Library(7, "Fotos") });
        await vm.LoadAutoExtractSettingsAsync();

        vm.IsAutoExtractEnabled = true;

        vm.ErrorMessage.Should().Contain("Automatisches Entpacken").And.Contain("Datenbank gesperrt");
        vm.IsErrorOpen.Should().BeTrue();
        vm.IsAutoExtractEnabled.Should().BeFalse("der gespeicherte Stand gilt weiter");
        service.EnabledCalls.Should().Equal((7, true));
    }

    [Fact]
    public async Task Ohne_Dienst_oder_ohne_aktive_Bibliothek_bleibt_der_Abschnitt_deaktiviert()
    {
        var noService = Create(null, new FakeLibraries { Active = Library(7, "Fotos") });
        await noService.LoadAutoExtractSettingsAsync();
        noService.IsAutoExtractEnabled = true;
        noService.IsAutoExtractAvailable.Should().BeFalse();
        noService.ErrorMessage.Should().BeNull();

        var service = new FakeAutoExtract();
        var noLibrary = Create(service, new FakeLibraries { Active = null });
        await noLibrary.LoadAutoExtractSettingsAsync();
        noLibrary.IsAutoExtractEnabled = true;

        noLibrary.IsAutoExtractAvailable.Should().BeFalse();
        noLibrary.AutoExtractLibraryText.Should().Contain("Keine Bibliothek");
        service.EnabledCalls.Should().BeEmpty("ohne Bibliothek wird nichts gespeichert");
        noLibrary.ExtractExistingArchivesCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Vorhandene_Archive_nachholen_fragt_nach_und_reiht_fuer_die_aktive_Bibliothek_ein()
    {
        var service = new FakeAutoExtract { ExistingCount = 3 };
        var dialogs = new FakeDialogs();
        var vm = Create(service, new FakeLibraries { Active = Library(7, "Fotos") }, dialogs);
        await vm.LoadAutoExtractSettingsAsync();
        vm.ExtractExistingArchivesCommand.CanExecute(null).Should().BeFalse("erst bei eingeschalteter Einstellung");

        vm.IsAutoExtractEnabled = true;
        vm.ExtractExistingArchivesCommand.CanExecute(null).Should().BeTrue();

        // Abbrechen → nichts eingereiht
        dialogs.ConfirmAnswer = false;
        await vm.ExtractExistingArchivesCommand.ExecuteAsync(null);
        dialogs.Confirmations.Should().ContainSingle().Which.Should().Contain("3 Archive").And.Contain("bleiben liegen");
        service.ExtractExistingCalls.Should().BeEmpty();

        // Bestätigen → eingereiht, Hinweis
        dialogs.ConfirmAnswer = true;
        vm.IsAutoExtractRecycle = true;
        await vm.ExtractExistingArchivesCommand.ExecuteAsync(null);
        dialogs.Confirmations.Last().Should().Contain("Papierkorb");
        service.ExtractExistingCalls.Should().Equal(7);
        vm.InfoMessage.Should().Contain("3 Archive werden entpackt");
        vm.IsExtractingExisting.Should().BeFalse();

        // Nichts vorhanden → kein Dialog, nur Hinweis
        service.ExistingCount = 0;
        await vm.ExtractExistingArchivesCommand.ExecuteAsync(null);
        dialogs.Confirmations.Should().HaveCount(2);
        service.ExtractExistingCalls.Should().Equal(7);
        vm.InfoMessage.Should().StartWith("Keine vorhandenen Archive");
    }

    [Fact]
    public void Statusleiste_beschreibt_Fortschritt_Erfolg_und_gemeldete_Archive()
    {
        var running = StatusBarViewModel.DescribeAutoExtract(new ArchiveAutoExtractStatus(1, ArchiveAutoExtractState.Running, 2, 3, "urlaub.zip"));
        running.Text.Should().Be("Archiv 2/3 wird automatisch entpackt: urlaub.zip");
        running.IsWarning.Should().BeFalse();

        var ok = new ArchiveAutoExtractSummary(1,
            [new ArchiveAutoExtractItem(7, @"C:\Eingang\urlaub.zip", ArchiveAutoExtractItemState.Extracted, @"C:\Eingang\urlaub", 12, true, null)],
            null, null, false);
        var done = StatusBarViewModel.DescribeAutoExtract(new ArchiveAutoExtractStatus(1, ArchiveAutoExtractState.Finished, 1, 1, null, ok));
        done.Text.Should().Be("Automatisch entpackt: 1 Archiv · 12 Dateien · 1 im Papierkorb");
        done.IsWarning.Should().BeFalse();
        done.Details.Should().BeNull();

        var mixed = new ArchiveAutoExtractSummary(1,
            [
                new ArchiveAutoExtractItem(7, @"C:\Eingang\urlaub.zip", ArchiveAutoExtractItemState.Extracted, @"C:\Eingang\urlaub", 12, false, null),
                new ArchiveAutoExtractItem(8, @"C:\Eingang\geheim.rar", ArchiveAutoExtractItemState.Failed, null, 0, false, "Das Archiv „geheim.rar“ ist passwortgeschützt und kann nicht entpackt werden."),
            ],
            null, null, false);
        var warn = StatusBarViewModel.DescribeAutoExtract(new ArchiveAutoExtractStatus(1, ArchiveAutoExtractState.Finished, 2, 2, null, mixed));
        warn.Text.Should().Be("Automatisch entpackt: 1 Archiv · 12 Dateien · 1 nicht entpackt");
        warn.IsWarning.Should().BeTrue();
        warn.Details.Should().Contain("geheim.rar").And.Contain("passwortgeschützt");

        var onlyFailed = new ArchiveAutoExtractSummary(1, [mixed.Items[1]], null, null, false);
        var failed = StatusBarViewModel.DescribeAutoExtract(new ArchiveAutoExtractStatus(1, ArchiveAutoExtractState.Finished, 1, 1, null, onlyFailed));
        failed.Text.Should().StartWith("Archiv nicht automatisch entpackt: geheim.rar — ");
        failed.IsWarning.Should().BeTrue();
    }
}
