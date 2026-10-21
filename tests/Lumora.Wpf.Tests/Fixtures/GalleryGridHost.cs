using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Lumora.Gallery.Controls;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;

namespace Lumora.Wpf.Tests.Fixtures;

/// <summary>
/// Echtes Fenster mit einer <see cref="GalleryListBox"/> (WPF-UI-Stil aus <see cref="TestUiThread"/>), einer
/// kachelähnlichen Datenvorlage (rund ein Dutzend Elemente wie die echte Galerie-Kachel), dem Container-Stil mit
/// zweiseitiger <c>IsSelected</c>-Bindung und <see cref="GalleryItem"/>-Dummies. Nur auf dem Test-UI-Thread verwenden.
/// </summary>
internal sealed class GalleryGridHost : IDisposable
{
    public const double TileWidth = 180;
    public const double TileHeight = 160;

    private const string TileTemplateXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
          <Grid Background="Transparent">
            <Grid.RowDefinitions>
              <RowDefinition Height="*" />
              <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <Grid Grid.Row="0" ClipToBounds="True">
              <ui:SymbolIcon Symbol="Image24" FontSize="48" HorizontalAlignment="Center" VerticalAlignment="Center" />
              <Image Source="{Binding Thumbnail}" Stretch="UniformToFill" />
              <Border HorizontalAlignment="Left" VerticalAlignment="Bottom" Margin="4,0,0,4" Padding="6,1" CornerRadius="3" Background="#8000A0FF">
                <TextBlock Text="{Binding Extension}" FontSize="10" />
              </Border>
              <Button HorizontalAlignment="Right" VerticalAlignment="Top" Margin="4" Padding="4" Content="♥" Focusable="False" />
              <Border HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="0,0,4,4" Padding="4,2" Background="#66000000">
                <TextBlock Text="★★★★★" FontSize="11" />
              </Border>
            </Grid>
            <Border Grid.Row="1" Padding="8,4">
              <StackPanel>
                <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" FontSize="11" />
                <TextBlock Text="{Binding SizeDisplay}" FontSize="10" />
              </StackPanel>
            </Border>
          </Grid>
        </DataTemplate>
        """;

    private const string ContainerStyleXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="ListBoxItem">
          <Setter Property="IsSelected" Value="{Binding IsSelected, Mode=TwoWay}" />
          <Setter Property="Margin" Value="0" />
          <Setter Property="Padding" Value="0" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Grid Margin="6">
                  <Border x:Name="Frame" BorderThickness="2" BorderBrush="Transparent" CornerRadius="6">
                    <ContentPresenter />
                  </Border>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="Frame" Property="BorderBrush" Value="DodgerBlue" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    public GalleryGridHost(int itemCount, bool virtualizing = true, double width = 900, double height = 600)
    {
        Items = new ObservableCollection<GalleryItem>(Enumerable.Range(0, itemCount).Select(CreateItem));
        ListBox = new GalleryListBox
        {
            ItemTemplate = (DataTemplate)XamlReader.Parse(TileTemplateXaml),
            ItemContainerStyle = (Style)XamlReader.Parse(ContainerStyleXaml),
            SelectionMode = SelectionMode.Extended,
            Padding = new Thickness(8),
            BorderThickness = new Thickness(0),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(ListBox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(ListBox, ScrollBarVisibility.Auto);

        if (virtualizing)
        {
            ScrollViewer.SetCanContentScroll(ListBox, true);
            VirtualizingPanel.SetIsVirtualizing(ListBox, true);
            VirtualizingPanel.SetVirtualizationMode(ListBox, VirtualizationMode.Recycling);
            ListBox.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingTilePanel)));
        }
        else
        {
            // Stand 0.2.2: nicht virtualisierendes WrapPanel mit Pixel-Scrolling des ScrollViewers.
            ScrollViewer.SetCanContentScroll(ListBox, false);
            var factory = new FrameworkElementFactory(typeof(WrapPanel));
            factory.SetValue(WrapPanel.ItemWidthProperty, TileWidth);
            factory.SetValue(WrapPanel.ItemHeightProperty, TileHeight);
            ListBox.ItemsPanel = new ItemsPanelTemplate(factory);
        }

        Window = new Window
        {
            Title = "Lumora.Wpf.Tests – Kachel-Grid",
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -5000, // außerhalb des sichtbaren Bereichs
            Top = -5000,
            Content = ListBox,
        };
    }

    public ObservableCollection<GalleryItem> Items { get; }

    public GalleryListBox ListBox { get; }

    public Window Window { get; }

    public VirtualizingTilePanel Panel => FindVisualChild<VirtualizingTilePanel>(ListBox)
        ?? throw new InvalidOperationException("VirtualizingTilePanel nicht im Visual-Baum — Vorlage noch nicht angewendet?");

    /// <summary>Zeigt das Fenster, bindet die Items und wartet den ersten Layoutdurchlauf ab.</summary>
    public void ShowAndLayout()
    {
        Window.Show();
        ListBox.ItemsSource = Items;
        Window.UpdateLayout();
        Pump(DispatcherPriority.Loaded);
    }

    public IReadOnlyList<ListBoxItem> RealizedContainers()
    {
        var generator = ListBox.ItemContainerGenerator;
        var result = new List<ListBoxItem>();
        for (var i = 0; i < Items.Count; i++)
        {
            if (generator.ContainerFromIndex(i) is ListBoxItem container)
            {
                result.Add(container);
            }
        }

        return result;
    }

    public IReadOnlyList<int> RealizedIndexes()
    {
        var generator = ListBox.ItemContainerGenerator;
        var result = new List<int>();
        for (var i = 0; i < Items.Count; i++)
        {
            if (generator.ContainerFromIndex(i) is not null)
            {
                result.Add(i);
            }
        }

        return result;
    }

    public ListBoxItem? ContainerOf(int index) => ListBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;

    /// <summary>Oberer Rand des Containers relativ zur ListBox (negativ = oberhalb des sichtbaren Bereichs).</summary>
    public double TopOf(ListBoxItem container) => container.TranslatePoint(new Point(0, 0), ListBox).Y;

    /// <summary>Arbeitet die Dispatcher-Warteschlange bis einschließlich <paramref name="priority"/> ab (verschachtelte Nachrichtenschleife).</summary>
    public static void Pump(DispatcherPriority priority)
        => Dispatcher.CurrentDispatcher.Invoke(() => { }, priority);

    public static GalleryItem CreateItem(int index)
        => new(new GalleryFileDto(
            Id: index + 1,
            FolderId: 1,
            Name: $"IMG_{index:0000}.jpg",
            Extension: ".jpg",
            Size: 1_234_567 + index,
            ModifiedAtUnix: 1_700_000_000 + index,
            MediaType: 0,
            IsFavorite: false,
            Rating: null,
            Missing: false,
            FolderPath: @"C:\Testbibliothek"));

    public static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    public void Dispose()
    {
        ListBox.ItemsSource = null;
        Window.Close();
    }
}
