using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using PhotoViewerV3.Spike03.ViewModels;

namespace PhotoViewerV3.Spike03.Views;

public partial class SpikeLightboxView : UserControl
{
    public static readonly DependencyProperty OpenInWindowCommandProperty =
        DependencyProperty.Register(
            nameof(OpenInWindowCommand),
            typeof(IRelayCommand),
            typeof(SpikeLightboxView));

    public SpikeLightboxView(SpikeLightboxViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        OpenInWindowCommand = new RelayCommand(OpenInWindow);
    }

    public IRelayCommand? OpenInWindowCommand
    {
        get => (IRelayCommand?)GetValue(OpenInWindowCommandProperty);
        set => SetValue(OpenInWindowCommandProperty, value);
    }

    private void OpenInWindow()
    {
        if (DataContext is not SpikeLightboxViewModel vm)
        {
            return;
        }

        var window = new LightboxWindow(vm)
        {
            Owner = Window.GetWindow(this),
        };
        window.Show();
        vm.NotifyOpenedInWindow();
    }
}
