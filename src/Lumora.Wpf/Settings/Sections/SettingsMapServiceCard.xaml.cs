using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Lumora.Places.Styles;

namespace Lumora.Settings.Sections;

/// <summary>
/// Karte „Kartendienst“ (ADR-0055). Code-Behind nur für das Passwortfeld (in WPF nicht bindbar): Eingabe →
/// <see cref="MapServiceAccountViewModel.Key"/>, gespeicherter Wert → Feld.
/// </summary>
public partial class SettingsMapServiceCard : UserControl
{
    private MapServiceAccountViewModel? _viewModel;

    public SettingsMapServiceCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as MapServiceAccountViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncBox();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapServiceAccountViewModel.Key))
        {
            SyncBox();
        }
    }

    private void SyncBox()
    {
        if (_viewModel is not null && KeyBox.Password != (_viewModel.Key ?? string.Empty))
        {
            KeyBox.Password = _viewModel.Key ?? string.Empty;
        }
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && _viewModel.Key != KeyBox.Password)
        {
            _viewModel.Key = KeyBox.Password;
        }
    }
}
