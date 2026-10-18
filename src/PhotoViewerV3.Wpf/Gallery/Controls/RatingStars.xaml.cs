using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoViewerV3.Gallery.Controls;

/// <summary>
/// Fünf klickbare Sterne. Klick setzt die Bewertung, Klick auf den bereits gesetzten Wert löscht
/// sie (Rating null). Die Änderung wird nicht direkt in <see cref="Value"/> geschrieben, sondern
/// als <see cref="RatingRequest"/> an <see cref="Command"/> gemeldet — das ViewModel entscheidet.
/// </summary>
public partial class RatingStars : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(byte?), typeof(RatingStars), new PropertyMetadata(null, OnValueChanged));

    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(byte?), typeof(RatingStars), new PropertyMetadata(null));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(RatingStars), new PropertyMetadata(null));

    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(
        nameof(CommandParameter), typeof(object), typeof(RatingStars), new PropertyMetadata(null));

    public static readonly DependencyProperty StarSizeProperty = DependencyProperty.Register(
        nameof(StarSize), typeof(double), typeof(RatingStars), new PropertyMetadata(12.0));

    public static readonly DependencyProperty FilledBrushProperty = DependencyProperty.Register(
        nameof(FilledBrush), typeof(Brush), typeof(RatingStars), new PropertyMetadata(Brushes.Gold));

    public static readonly DependencyProperty EmptyBrushProperty = DependencyProperty.Register(
        nameof(EmptyBrush), typeof(Brush), typeof(RatingStars), new PropertyMetadata(Brushes.LightGray));

    private bool _isHovering;

    public RatingStars()
    {
        InitializeComponent();
    }

    /// <summary>Aktuelle Bewertung 1–5 oder null.</summary>
    public byte? Value
    {
        get => (byte?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Angezeigter Wert (Hover-Vorschau oder <see cref="Value"/>).</summary>
    public byte? DisplayValue
    {
        get => (byte?)GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public double StarSize
    {
        get => (double)GetValue(StarSizeProperty);
        set => SetValue(StarSizeProperty, value);
    }

    public Brush FilledBrush
    {
        get => (Brush)GetValue(FilledBrushProperty);
        set => SetValue(FilledBrushProperty, value);
    }

    public Brush EmptyBrush
    {
        get => (Brush)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (RatingStars)d;
        if (!control._isHovering)
        {
            control.DisplayValue = (byte?)e.NewValue;
        }
    }

    private static bool TryGetStar(object sender, out byte star)
    {
        star = 0;
        return sender is Button { Tag: string tag }
               && byte.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out star);
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetStar(sender, out var clicked))
        {
            return;
        }

        e.Handled = true;
        byte? newValue = Value == clicked ? null : clicked;
        var request = new RatingRequest(CommandParameter, newValue);
        if (Command?.CanExecute(request) == true)
        {
            Command.Execute(request);
        }
    }

    private void OnStarEnter(object sender, MouseEventArgs e)
    {
        if (!TryGetStar(sender, out var star))
        {
            return;
        }

        _isHovering = true;
        DisplayValue = star;
    }

    private void OnStarsLeave(object sender, MouseEventArgs e)
    {
        _isHovering = false;
        DisplayValue = Value;
    }
}
