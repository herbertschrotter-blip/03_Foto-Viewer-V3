using System.Globalization;
using System.Windows;
using Lumora.Shell;

namespace Lumora.Wpf.Tests.Shell;

public sealed class ConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Hinweis", true)]
    [InlineData(42, true)]
    public void NullToBool_ReturnsBool_ForInfoBarIsOpen(object? value, bool expected)
    {
        var converter = new NullToBoolConverter();

        converter.Convert(value, typeof(bool), null, Culture).Should().Be(expected);
        new NullToBoolConverter { Invert = true }.Convert(value, typeof(bool), null, Culture).Should().Be(!expected);
    }

    [Theory]
    [InlineData(null, Visibility.Collapsed)]
    [InlineData("", Visibility.Collapsed)]
    [InlineData("x", Visibility.Visible)]
    public void NullToVisibility(object? value, Visibility expected)
        => new NullToVisibilityConverter().Convert(value, typeof(Visibility), null, Culture).Should().Be(expected);

    [Fact]
    public void BoolToVisibility_InvertAndFalseVisibility()
    {
        new BoolToVisibilityConverter().Convert(true, typeof(Visibility), null, Culture).Should().Be(Visibility.Visible);
        new BoolToVisibilityConverter().Convert(false, typeof(Visibility), null, Culture).Should().Be(Visibility.Collapsed);
        new BoolToVisibilityConverter { Invert = true }.Convert(false, typeof(Visibility), null, Culture).Should().Be(Visibility.Visible);
        new BoolToVisibilityConverter { FalseVisibility = Visibility.Hidden }.Convert(false, typeof(Visibility), null, Culture).Should().Be(Visibility.Hidden);
    }
}
