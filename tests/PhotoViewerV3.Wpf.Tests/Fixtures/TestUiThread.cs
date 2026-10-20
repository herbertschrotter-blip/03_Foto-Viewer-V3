using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace PhotoViewerV3.Wpf.Tests.Fixtures;

/// <summary>
/// Ein dauerhafter STA-Thread mit laufendem Dispatcher und einer WPF-<see cref="Application"/> samt
/// WPF-UI-Theme-Ressourcen, damit echte Fenster (z. B. Dialoge) in Tests geöffnet werden können.
/// Alle UI-Tests laufen auf demselben Thread; ein hängendes Fenster lässt den Aufruf per Timeout scheitern.
/// Testklassen, die ihn benutzen, gehören in die Sammlung <see cref="UiTestCollection"/> (wird beim Zugriff geprüft).
/// </summary>
internal static class TestUiThread
{
    private static readonly Lazy<Dispatcher> Instance = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Dispatcher Dispatcher
    {
        get
        {
            EnsureCallerIsInUiCollection();
            return Instance.Value;
        }
    }

    public static T Run<T>(Func<T> action, TimeSpan timeout)
    {
        var operation = Dispatcher.InvokeAsync(action);
        if (!operation.Task.Wait(timeout))
        {
            throw new TimeoutException($"UI-Aktion nicht innerhalb von {timeout.TotalSeconds:N0} s abgeschlossen — Fenster hängt?");
        }

        return operation.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Sucht auf dem Aufrufstapel die Testklasse (äußerster Typ mit [Fact]/[Theory]-Methoden — Lambdas und async-
    /// Zustandsautomaten sind darin verschachtelt) und verlangt <c>[Collection(UiTestCollection.Name)]</c>. Ohne die
    /// Sammlung liefe die Klasse parallel zu anderen UI-Tests auf demselben Dispatcher (siehe <see cref="UiTestCollection"/>);
    /// so scheitert der Fehler sofort und immer statt gelegentlich.
    /// </summary>
    private static void EnsureCallerIsInUiCollection()
    {
        foreach (var frame in new StackTrace().GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            while (type?.DeclaringType is not null)
            {
                type = type.DeclaringType;
            }

            if (type is null || type.Assembly != typeof(TestUiThread).Assembly || !IsTestClass(type))
            {
                continue;
            }

            var inCollection = type.GetCustomAttributesData().Any(a =>
                a.AttributeType == typeof(CollectionAttribute)
                && a.ConstructorArguments is [{ Value: UiTestCollection.Name }]);
            if (!inCollection)
            {
                throw new InvalidOperationException(
                    $"{type.Name} benutzt den gemeinsamen Test-UI-Thread, steht aber nicht in [Collection(UiTestCollection.Name)].");
            }

            return;
        }
    }

    /// <summary>
    /// Verwirft echte Tastatur- und Maus-Eingaben auf dem Test-UI-Thread. <c>ShowDialog</c> übergeht
    /// <c>ShowActivated = false</c>: Ein Testdialog wird — auch außerhalb des Bildschirms — Vordergrundfenster, das
    /// Eingabefeld hat den Tastaturfokus mit markiertem Text. Tippt der Benutzer in diesem Moment (z. B. Rücktaste in
    /// seiner eigenen App), landet das im Test und leert die Eingabe. Kein Test nutzt echte Eingaben (Knöpfe werden über
    /// Methoden „gedrückt“); Fokuswechsel und per <c>RaiseEvent</c> ausgelöste Ereignisse bleiben unberührt.
    /// </summary>
    private static void DropUserInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyEventArgs or TextCompositionEventArgs or MouseButtonEventArgs or MouseWheelEventArgs)
        {
            e.Cancel();
        }
    }

    private static bool IsTestClass(Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Any(m => m.IsDefined(typeof(FactAttribute), inherit: true));

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

                InputManager.Current.PreProcessInput += DropUserInput;
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
