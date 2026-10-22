using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lumora.Stories;

/// <summary>Seite „Storys“ (ADR-0060); Logik im <see cref="StoriesViewModel"/>, hier nur Breite und sichtbare Reihen.</summary>
public partial class StoriesPage : UserControl
{
    public StoriesPage(StoriesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
        Loaded += (_, _) => _ = viewModel.EnsureLoadedAsync();
        // Story-Assistent (ADR-0063): höchstens zwei Drittel der Seite, damit die Übersicht sichtbar bleibt
        OverviewRoot.SizeChanged += (_, e) => AssistantScroll.MaxHeight = Math.Max(160, e.NewSize.Height * 0.66);
    }

    public StoriesViewModel ViewModel { get; }

    /// <summary>Breite der Kartenreihen = Liste minus Bildlaufleiste und rechter Rand der Karten.</summary>
    private void OnOverviewSizeChanged(object sender, SizeChangedEventArgs e)
        => ViewModel.SetLayoutWidth(e.NewSize.Width - 18 - StoriesViewModel.CardGap); // jede Karte hat rechts 16 Rand

    /// <summary>Eine Kartenreihe wurde realisiert (sichtbar): Bilder und Personen ihrer Karten laden.</summary>
    private void OnCardRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverviewCardRow row })
        {
            _ = ViewModel.RequestRowAsync(row);
        }
    }

    private void OnMemoryRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverviewMemoryRow row })
        {
            _ = ViewModel.RequestMemoryAsync(row.Memory);
        }
    }
}

/// <summary>Kleine Konverter der Story-Seite.</summary>
public static class StoryVisibility
{
    /// <summary>Alle Werte true → Visible, sonst Collapsed.</summary>
    public static IMultiValueConverter AllTrue { get; } = new AllTrueConverter();

    /// <summary>bool umkehren.</summary>
    public static IValueConverter Not { get; } = new NotConverter();

    private sealed class AllTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    }
}
