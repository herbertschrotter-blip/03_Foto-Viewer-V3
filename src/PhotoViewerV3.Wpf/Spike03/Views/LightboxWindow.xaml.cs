using System.Windows;
using System.Windows.Input;
using PhotoViewerV3.Spike03.ViewModels;
using Wpf.Ui.Controls;

namespace PhotoViewerV3.Spike03.Views;

public partial class LightboxWindow : FluentWindow
{
    private WindowState _previousState;
    private WindowStyle _previousStyle;
    private bool _isFullscreen;

    public LightboxWindow(SpikeLightboxViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_isFullscreen)
                {
                    ExitFullscreen();
                }
                else
                {
                    Close();
                }
                e.Handled = true;
                break;
            case Key.F:
            case Key.F11:
                if (_isFullscreen)
                {
                    ExitFullscreen();
                }
                else
                {
                    EnterFullscreen();
                }
                e.Handled = true;
                break;
        }
    }

    private void EnterFullscreen()
    {
        _previousState = WindowState;
        _previousStyle = WindowStyle;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;
        _isFullscreen = true;
    }

    private void ExitFullscreen()
    {
        WindowStyle = _previousStyle;
        ResizeMode = ResizeMode.CanResize;
        WindowState = _previousState;
        _isFullscreen = false;
    }
}
