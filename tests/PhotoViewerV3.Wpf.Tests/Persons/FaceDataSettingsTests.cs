using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Lightbox.Faces;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Services.Faces.Erase;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Settings.FaceData;

namespace PhotoViewerV3.Wpf.Tests.Persons;

/// <summary>
/// Einstellungen → „Gesichtsdaten löschen…“ (0.2.38): nur nach Bestätigung, mit deutlichem Text und für die aktive
/// Bibliothek; Fehler werden gemeldet.
/// </summary>
public sealed class FaceDataSettingsTests
{
    [Fact]
    public async Task Loescht_erst_nach_Bestaetigung_fuer_die_aktive_Bibliothek()
    {
        var eraser = new RecordingEraser();
        var libraries = ActiveLibrary.Create(new LibraryInfo(5, "Familie", @"D:\Fotos", DateTime.UtcNow, null, 0, 0, true, []));

        var declined = new FakePersonDialogs(confirm: false);
        var vm = new FaceDataSettingsViewModel(eraser, libraries, declined, NullLogger<FaceDataSettingsViewModel>.Instance);
        await vm.EraseCommand.ExecuteAsync(null);
        eraser.Calls.Should().BeEmpty("ohne Bestätigung wird nichts gelöscht");
        declined.LastTitle.Should().Be(FaceDataSettingsViewModel.ConfirmTitle);
        declined.LastMessage.Should().Contain("„Familie“").And.Contain("Fingerabdrücken").And.Contain("Personen")
            .And.Contain("abgelehnte Vorschläge").And.Contain("Gesichter-Cache").And.Contain("Fotos selbst").And.Contain("nicht rückgängig");

        var vm2 = new FaceDataSettingsViewModel(eraser, libraries, new FakePersonDialogs(confirm: true), NullLogger<FaceDataSettingsViewModel>.Instance);
        await vm2.EraseCommand.ExecuteAsync(null);
        eraser.Calls.Should().Equal(5);
        vm2.MessageIsError.Should().BeFalse();
        vm2.Message.Should().Contain("3 Gesichter").And.Contain("ausgeschaltet");
        vm2.IsErasing.Should().BeFalse();
        FaceDataSettingsViewModel.PrivacyText.Should().Contain("Alles bleibt auf diesem PC");
    }

    [Fact]
    public async Task Fehler_und_fehlende_Bibliothek_werden_gemeldet()
    {
        var failing = new RecordingEraser { Fail = true };
        var vm = new FaceDataSettingsViewModel(failing, ActiveLibrary.Create(new LibraryInfo(1, "X", "C:\\", DateTime.UtcNow, null, 0, 0, true, [])),
            new FakePersonDialogs(), NullLogger<FaceDataSettingsViewModel>.Instance);
        await vm.EraseCommand.ExecuteAsync(null);
        vm.MessageIsError.Should().BeTrue();
        vm.Message.Should().Contain("Platte voll");

        var none = new FaceDataSettingsViewModel(failing, ActiveLibrary.Create(null), new FakePersonDialogs(), NullLogger<FaceDataSettingsViewModel>.Instance);
        await none.EraseCommand.ExecuteAsync(null);
        none.Message.Should().Be("Keine Bibliothek ausgewählt.");
    }

    private sealed class RecordingEraser : IFaceDataEraser
    {
        public List<int> Calls { get; } = new();

        public bool Fail { get; init; }

        public Task<FaceDataEraseResult> EraseLibraryAsync(int libraryId, CancellationToken ct = default)
        {
            Calls.Add(libraryId);
            return Fail
                ? Task.FromException<FaceDataEraseResult>(new IOException("Platte voll"))
                : Task.FromResult(new FaceDataEraseResult(3, 2, 1, 1, 0));
        }
    }

    /// <summary><see cref="ILibraryService"/>, der nur die aktive Bibliothek kennt (übrige Methoden: Standardwerte).</summary>
    public class ActiveLibrary : DispatchProxy
    {
        private LibraryInfo? _active;

        public static ILibraryService Create(LibraryInfo? active)
        {
            var proxy = DispatchProxy.Create<ILibraryService, ActiveLibrary>();
            ((ActiveLibrary)(object)proxy)._active = active;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(ILibraryService.GetActiveAsync)
                ? Task.FromResult(_active)
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
