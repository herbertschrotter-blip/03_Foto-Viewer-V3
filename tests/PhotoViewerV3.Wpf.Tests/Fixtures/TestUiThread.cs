using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace PhotoViewerV3.Wpf.Tests.Fixtures;

/// <summary>
/// Ein dauerhafter STA-Thread mit laufendem Dispatcher und einer WPF-<see cref="Application"/> samt
/// WPF-UI-Theme-Ressourcen, damit echte Fenster (z. B. Dialoge) in Tests geöffnet werden können.
/// Alle UI-Tests laufen auf demselben Thread; ein hängendes Fenster lässt den Aufruf per Timeout scheitern.
/// </summary>
internal static class TestUiThread
{
    private static readonly Lazy<Dispatcher> Instance = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Dispatcher Dispatcher => Instance.Value;

    public static T Run<T>(Func<T> action, TimeSpan timeout)
    {
        var operation = Dispatcher.InvokeAsync(action);
        if (!operation.Task.Wait(timeout))
        {
            throw new TimeoutException($"UI-Aktion nicht innerhalb von {timeout.TotalSeconds:N0} s abgeschlossen — Fenster hängt?");
        }

        return operation.Task.GetAwaiter().GetResult();
    }

    private static Dispatcher Start()
    {
        Dispatcher? created = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
                    app.Resources.MergedDictionaries.Add(new ControlsDictionary());
                }

                created = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }

            if (failure is null)
            {
                Dispatcher.Run();
            }
        })
        {
            IsBackground = true,
            Name = "TestUiThread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (failure is not null)
        {
            throw new InvalidOperationException("UI-Thread konnte nicht gestartet werden", failure);
        }

        return created!;
    }
}
