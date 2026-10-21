using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Tags;
using Lumora.Settings.TagManagement;
using Lumora.Shell;

namespace Lumora.Wpf.Tests.Tags;

/// <summary>Seite „Tags“: Laden mit Zählern, Anlegen, Fachfehler als InfoBar, Reihenfolge, Farbe, Löschen nur nach Bestätigung.</summary>
public sealed class TagManagementViewModelTests
{
    [Fact]
    public async Task Laden_zeigt_Tags_mit_Zaehlern_und_Positionen()
    {
        var (vm, service, _) = Create();
        service.Seed("Urlaub", 3);
        service.Seed("Familie", 0);

        await vm.LoadCommand.ExecuteAsync(null);

        vm.Tags.Select(t => t.Name).Should().Equal("Urlaub", "Familie");
        vm.Tags[0].FileCountText.Should().Be("3 Dateien");
        vm.Tags[0].CanMoveUp.Should().BeFalse();
        vm.Tags[1].CanMoveDown.Should().BeFalse();
        vm.Tags[0].ColorChoices.Should().HaveCount(TagPalette.Colors.Count).And.ContainSingle(c => c.IsCurrent);
        vm.SummaryText.Should().Contain("2 Tag(s)").And.Contain("3 Zuweisung(en)");
        vm.HasNoTags.Should().BeFalse();
    }

    [Fact]
    public async Task Anlegen_und_Duplikat_als_Fehlermeldung()
    {
        var (vm, service, _) = Create();
        vm.CreateCommand.CanExecute(null).Should().BeFalse("leerer Name");

        vm.NewTagName = "Urlaub";
        await vm.CreateCommand.ExecuteAsync(null);
        vm.Tags.Should().ContainSingle(t => t.Name == "Urlaub");
        vm.NewTagName.Should().BeEmpty();
        vm.IsInfoOpen.Should().BeTrue();

        vm.NewTagName = "urlaub";
        await vm.CreateCommand.ExecuteAsync(null);
        vm.IsErrorOpen.Should().BeTrue();
        vm.ErrorMessage.Should().Contain("existiert bereits");
        service.Tags.Should().HaveCount(1);
    }

    [Fact]
    public async Task Nach_unten_tauscht_die_Reihenfolge()
    {
        var (vm, service, _) = Create();
        service.Seed("A", 0);
        service.Seed("B", 0);
        service.Seed("C", 0);
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.MoveDownCommand.ExecuteAsync(vm.Tags[0]);

        vm.Tags.Select(t => t.Name).Should().Equal("B", "A", "C");
    }

    [Fact]
    public async Task Farbe_setzen_ueber_die_Palette()
    {
        var (vm, service, _) = Create();
        service.Seed("A", 0);
        await vm.LoadCommand.ExecuteAsync(null);
        var green = vm.Tags[0].ColorChoices.First(c => c.Color == "#43A047");

        await vm.SetColorCommand.ExecuteAsync(green);

        service.Tags[0].Color.Should().Be("#43A047");
        vm.Tags[0].ColorChoices.Single(c => c.IsCurrent).Color.Should().Be("#43A047");
    }

    [Fact]
    public async Task Loeschen_fragt_mit_Zaehler_nach_und_loescht_nur_bei_Bestaetigung()
    {
        var (vm, service, dialogs) = Create();
        service.Seed("Urlaub", 5);
        await vm.LoadCommand.ExecuteAsync(null);

        dialogs.Answer = false;
        await vm.DeleteCommand.ExecuteAsync(vm.Tags[0]);
        dialogs.LastMessage.Should().Contain("5 Dateien");
        service.Tags.Should().HaveCount(1);

        dialogs.Answer = true;
        await vm.DeleteCommand.ExecuteAsync(vm.Tags[0]);
        service.Tags.Should().BeEmpty();
        vm.Tags.Should().BeEmpty();
        vm.InfoMessage.Should().Contain("5 Zuweisung(en) entfernt");
    }

    [Fact]
    public async Task Umbenennen_ueber_Eingabedialog()
    {
        var (vm, service, dialogs) = Create();
        service.Seed("Urlaub", 0);
        await vm.LoadCommand.ExecuteAsync(null);

        dialogs.PromptAnswer = "Reise";
        await vm.RenameCommand.ExecuteAsync(vm.Tags[0]);

        vm.Tags.Single().Name.Should().Be("Reise");
    }

    private static (TagManagementViewModel Vm, FakeTagService Service, FakeDialogService Dialogs) Create()
    {
        var service = new FakeTagService();
        var dialogs = new FakeDialogService();
        return (new TagManagementViewModel(service, dialogs, NullLogger<TagManagementViewModel>.Instance), service, dialogs);
    }

    private sealed class FakeTagService : ITagService
    {
        private readonly Dictionary<int, int> _counts = new();
        private int _nextId = 1;

        public List<TagInfo> Tags { get; } = new();

        public void Seed(string name, int count)
        {
            var tag = new TagInfo(_nextId++, name, TagPalette.ForIndex(Tags.Count), Tags.Count);
            Tags.Add(tag);
            _counts[tag.Id] = count;
        }

        public Task<IReadOnlyList<TagInfo>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TagInfo>>(Tags.OrderBy(t => t.SortOrder).ToList());

        public Task<TagInfo> CreateAsync(string name, string? color = null, CancellationToken ct = default)
        {
            var normalized = TagService.NormalizeName(name);
            if (Tags.Any(t => string.Equals(t.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Ein Tag „{normalized}“ existiert bereits.");
            }

            Seed(normalized, 0);
            return Task.FromResult(Tags[^1]);
        }

        public Task RenameAsync(int tagId, string newName, CancellationToken ct = default)
        {
            Replace(tagId, t => t with { Name = newName.Trim() });
            return Task.CompletedTask;
        }

        public Task SetColorAsync(int tagId, string color, CancellationToken ct = default)
        {
            Replace(tagId, t => t with { Color = TagPalette.Normalize(color) });
            return Task.CompletedTask;
        }

        public Task ReorderAsync(IReadOnlyList<int> tagIdsInOrder, CancellationToken ct = default)
        {
            for (var i = 0; i < tagIdsInOrder.Count; i++)
            {
                var order = i;
                Replace(tagIdsInOrder[i], t => t with { SortOrder = order });
            }

            return Task.CompletedTask;
        }

        public Task<int> DeleteAsync(int tagId, CancellationToken ct = default)
        {
            Tags.RemoveAll(t => t.Id == tagId);
            return Task.FromResult(_counts.Remove(tagId, out var count) ? count : 0);
        }

        public Task<IReadOnlyDictionary<int, int>> GetFileCountsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>(_counts));

        public Task<int> AssignAsync(IReadOnlyList<int> fileIds, IReadOnlyList<int> tagIds, CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> RemoveAsync(IReadOnlyList<int> fileIds, IReadOnlyList<int> tagIds, CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> RemoveForFilesAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetTagIdsForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(new Dictionary<int, IReadOnlyList<int>>());

        private void Replace(int id, Func<TagInfo, TagInfo> change)
        {
            var index = Tags.FindIndex(t => t.Id == id);
            Tags[index] = change(Tags[index]);
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool Answer { get; set; }

        public string? PromptAnswer { get; set; }

        public string? LastMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false, string cancelText = "Abbrechen")
        {
            LastMessage = message;
            return Task.FromResult(Answer);
        }

        public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string closeText = "Abbrechen", bool secondaryIsDestructive = false)
            => Task.FromResult(DialogChoice.None);

        public Task<string?> PromptTextAsync(string title, string message, string initialValue, string confirmText = "OK")
            => Task.FromResult(PromptAnswer);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public string? PickFolder(string title) => null;
    }
}
