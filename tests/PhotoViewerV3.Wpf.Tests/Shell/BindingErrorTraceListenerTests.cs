using System.Windows.Controls;
using System.Windows.Data;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Wpf.Tests.Shell;

/// <summary>Der Listener muss WPF-Binding-Fehler wirklich zählen — sonst wäre „0 Binding-Fehler“ im Rauchtest wertlos.</summary>
public sealed class BindingErrorTraceListenerTests
{
    [Fact]
    public void CountsBindingErrors()
    {
        var error = RunSta(() =>
        {
            BindingErrorTraceListener.Install();
            var before = BindingErrorTraceListener.ErrorCount;

            // Bindung auf einen Pfad, den der DataContext nicht hat → WPF meldet "BindingExpression path error".
            var text = new TextBlock { DataContext = new { Vorhanden = "x" } };
            BindingOperations.SetBinding(text, TextBlock.TextProperty, new Binding("GibtEsNicht"));
            _ = text.Text; // Bindung auswerten

            return BindingErrorTraceListener.ErrorCount - before;
        });

        error.Should().BeGreaterThan(0);
    }

    private static int RunSta(Func<int> action)
    {
        var result = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }
}
