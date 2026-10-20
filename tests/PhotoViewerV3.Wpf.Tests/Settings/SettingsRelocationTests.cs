using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Database.Relocation;
using PhotoViewerV3.Settings;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Wpf.Tests.Settings;

/// <summary>Einstellungen → Speicherorte → „Ändern…“: Dialogfluss rund um den DB-Umzug (ADR-0026).</summary>
public sealed class SettingsRelocationTests
{
    private const string Current = @"C:\Daten\PhotoViewerV3";
    private const string Target = @"E:\Katalog";

    private sealed class FakeRelocation : IDatabaseRelocationService
    {
        public RelocationTargetCheck Check { get; set; } = Ready();

        public RelocationResult Result { get; set; } = new(true, null, Target + @"\library.db");

        public RelocationMode? Mode { get; private set; }

        public string CurrentFolder => Current;

        public string DefaultFolder => Current;

        public Task<RelocationTargetCheck> CheckTargetAsync(string targetFolder, CancellationToken ct = default) => Task.FromResult(Check);

        public Task<RelocationResult> RelocateAsync(string targetFolder, RelocationMode mode, IProgress<RelocationProgress>? progress = null, CancellationToken ct = default)
        {
            Mode = mode;
            progress?.Report(new RelocationProgress("Datenbank kopieren"));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeRestart : IAppRestartService
    {
        public List<bool> Blocked { get; } = [];

        public int Restarts { get; private set; }

        public bool RestartSucceeds { get; set; } = true;

        public void SetInputBlocked(bool blocked) => Blocked.Add(blocked);

        public bool Restart()
        {
            Restarts++;
            return RestartSucceeds;
        }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool ConfirmAnswer { get; set; } = true;

        public DialogChoice Choice { get; set; } = DialogChoice.None;

        public List<(string Title, string Message)> Errors { get; } = [];

        public string? LastConfirmMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
        {
            LastConfirmMessage = message;
            return Task.FromResult(ConfirmAnswer);
        }

        public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
            => Task.FromResult(Choice);

        public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK")
            => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message)
        {
            Errors.Add((title, message));
            return Task.CompletedTask;
        }

        public string? PickFolder(string title) => Target;
    }

    private static RelocationTargetCheck Ready(bool cloud = false)
        => new(RelocationTargetState.Ready, Target, null, 1_000_000, 10_000_000, true, cloud, 42, null);

    private static RelocationTargetCheck Existing(bool valid)
        => new(RelocationTargetState.ExistingData, Target, null, 1_000_000, 10_000_000, true, false, 42,
            new ExistingCatalogInfo(true, valid, 2, 1234, DateTime.UtcNow, valid ? null : "Die Datei ist kein PhotoViewer-Katalog."));

    private static (SettingsViewModel Vm, FakeRelocation Relocation, FakeRestart Restart, FakeDialogs Dialogs) Create()
    {
        var relocation = new FakeRelocation();
        var restart = new FakeRestart();
        var dialogs = new FakeDialogs();
        // Nur LoadAsync bräuchte die übrigen Dienste — der Umzugsfluss kommt ohne sie aus.
        var vm = new SettingsViewModel(null!, null!, null!, null!, null!, null!, dialogs, null!, relocation, restart, NullLogger<SettingsViewModel>.Instance);
        return (vm, relocation, restart, dialogs);
    }

    [Fact]
    public async Task Erfolg_startet_die_App_neu_und_haelt_die_Seite_gesperrt()
    {
        var (vm, relocation, restart, dialogs) = Create();

        await vm.MoveDatabaseCommand.ExecuteAsync(null);

        relocation.Mode.Should().Be(RelocationMode.Move);
        dialogs.LastConfirmMessage.Should().Contain(Target).And.Contain("42");
        restart.Restarts.Should().Be(1);
        restart.Blocked.Should().Equal(true);
        vm.IsRelocating.Should().BeTrue("bis die neue Instanz läuft, bleibt der Umzug sichtbar");
        dialogs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Fehler_meldet_den_Grund_und_den_weiter_gueltigen_Ort()
    {
        var (vm, relocation, restart, dialogs) = Create();
        relocation.Result = new RelocationResult(false, "Die Kopie ist unvollständig: Tabelle Files hat 2 statt 3 Zeilen.", Current + @"\library.db");

        await vm.MoveDatabaseCommand.ExecuteAsync(null);

        restart.Restarts.Should().Be(0);
        restart.Blocked.Should().Equal(true, false);
        dialogs.Errors.Should().ContainSingle();
        dialogs.Errors[0].Title.Should().Be("Umzug fehlgeschlagen");
        dialogs.Errors[0].Message.Should().Contain("unvollständig").And.Contain(Current);
        vm.IsRelocating.Should().BeFalse();
        vm.MoveDatabaseCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Ungeeignetes_Ziel_zeigt_den_Grund_ohne_Umzug()
    {
        var (vm, relocation, restart, dialogs) = Create();
        relocation.Check = new RelocationTargetCheck(RelocationTargetState.Invalid, Target, "Netzlaufwerke … eignen sich nicht", 0, null, false, false, 0, null);

        await vm.MoveDatabaseCommand.ExecuteAsync(null);

        relocation.Mode.Should().BeNull();
        restart.Blocked.Should().BeEmpty();
        dialogs.Errors.Should().ContainSingle().Which.Message.Should().Contain("Netzlaufwerke");
    }

    [Theory]
    [InlineData(DialogChoice.Primary, RelocationMode.AdoptExisting)]
    [InlineData(DialogChoice.Secondary, RelocationMode.ReplaceExisting)]
    public async Task Vorhandener_Katalog_im_Ziel_uebernehmen_oder_ersetzen(DialogChoice choice, RelocationMode expected)
    {
        var (vm, relocation, _, dialogs) = Create();
        relocation.Check = Existing(valid: true);
        dialogs.Choice = choice;

        await vm.MoveDatabaseCommand.ExecuteAsync(null);

        relocation.Mode.Should().Be(expected);
    }

    [Fact]
    public async Task Vorhandener_Katalog_abgebrochen_aendert_nichts()
    {
        var (vm, relocation, restart, dialogs) = Create();
        relocation.Check = Existing(valid: true);
        dialogs.Choice = DialogChoice.None;

        await vm.MoveDatabaseCommand.ExecuteAsync(null);

        relocation.Mode.Should().BeNull();
        restart.Blocked.Should().BeEmpty();
        vm.IsRelocating.Should().BeFalse();
    }

    [Fact]
    public async Task Fremde_Datei_im_Ziel_kann_nur_ersetzt_werden()
    {
        var (vm, relocation, _, dialogs) = Create();
        relocation.Check = Existing(valid: false);

        await vm.MoveDatabaseCommand.ExecuteAsync(null);

        relocation.Mode.Should().Be(RelocationMode.ReplaceExisting);
        dialogs.LastConfirmMessage.Should().Contain("kein PhotoViewer-Katalog");
    }

    [Fact]
    public async Task OneDrive_Ziel_warnt_im_Bestaetigungsdialog()
    {
        var (vm, relocation, _, dialogs) = Create();
        relocation.Check = Ready(cloud: true);
        dialogs.ConfirmAnswer = false;

        await vm.MoveDatabaseCommand.ExecuteAsync(null);

        dialogs.LastConfirmMessage.Should().Contain("OneDrive");
        relocation.Mode.Should().BeNull();
    }
}
