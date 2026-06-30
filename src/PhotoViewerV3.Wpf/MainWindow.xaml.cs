using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using PhotoViewerV3.Spike02.Views;
using PhotoViewerV3.Spike03.Views;
using Serilog;
using Wpf.Ui.Controls;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace PhotoViewerV3;

public partial class MainWindow : FluentWindow
{
    private readonly IServiceProvider _services;
    private FrameworkElement? _placeholder;
    private GalleryView? _galleryView;
    private SpikeLightboxView? _lightboxView;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        ContentArea.Content = BuildPlaceholder(
            "Wähle einen Bereich links.\n\n'Gallery' zeigt das paged virtualisierte Grid auf der aktiven Library.");

        Loaded += MainWindow_OnLoaded;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.Information("MainWindow geladen, NavigationView vorhanden: {HasNav}", MainNav is not null);
        if (MainNav is not null)
        {
            MainNav.SelectionChanged += OnNavSelectionChanged;
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, RoutedEventArgs e)
    {
        var item = sender.SelectedItem as NavigationViewItem;
        Log.Information("NavigationView SelectionChanged: SelectedItem={Item} Tag={Tag}",
            item?.Content, item?.Tag);
        ApplySelection(item?.Tag as string, item?.Content?.ToString());
    }

    private void SpikeGalleryItem_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Log.Information("Spike-Gallery NavItem Click (PreviewMouseLeftButtonDown)");
        ApplySelection("spike-gallery", "Spike: Gallery");
    }

    private void SpikeLightboxItem_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Log.Information("Spike-Lightbox NavItem Click (PreviewMouseLeftButtonDown)");
        ApplySelection("spike-lightbox", "Spike: Lightbox");
    }

    private void ApplySelection(string? tag, string? label)
    {
        switch (tag)
        {
            case "spike-gallery":
                ContentArea.Content = _galleryView ??= _services.GetRequiredService<GalleryView>();
                return;
            case "spike-lightbox":
                ContentArea.Content = _lightboxView ??= _services.GetRequiredService<SpikeLightboxView>();
                return;
            default:
                ContentArea.Content = BuildPlaceholder($"'{label ?? "?"}' ist im V0.1-Backlog — noch nicht implementiert.");
                return;
        }
    }

    private FrameworkElement BuildPlaceholder(string message)
    {
        if (_placeholder is null)
        {
            _placeholder = new WpfTextBlock
            {
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 480,
            };
        }

        ((WpfTextBlock)_placeholder).Text = message;
        return _placeholder;
    }
}
