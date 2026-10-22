using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lumora.Stories.Assistant;

/// <summary>Eingabeleiste und Karten des Story-Assistenten (ADR-0063); Enter = „Los“, „Satz ändern“ fokussiert die Eingabe.</summary>
public partial class StoryAssistantPanel : UserControl
{
    private StoryAssistantViewModel? _viewModel;

    public StoryAssistantPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FocusRequested -= OnFocusRequested;
        }

        _viewModel = DataContext as StoryAssistantViewModel;
        if (_viewModel is not null)
        {
            _viewModel.FocusRequested += OnFocusRequested;
        }
    }

    private void OnFocusRequested()
    {
        SentenceBox.Focus();
        SentenceBox.CaretIndex = SentenceBox.Text?.Length ?? 0;
    }

    private void OnSentenceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel is { IsIdle: true } vm)
        {
            e.Handled = true;
            _ = vm.AskCommand.ExecuteAsync(null);
        }
    }
}
