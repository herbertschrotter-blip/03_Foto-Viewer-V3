namespace PhotoViewerV3.Wpf.Tests.Fixtures;

/// <summary>
/// xUnit-Sammlung für alle Testklassen, die den gemeinsamen <see cref="TestUiThread"/> benutzen. Die Klassen laufen
/// nacheinander und nie parallel zu anderen Sammlungen.
/// <para>
/// Hintergrund: Es gibt nur einen UI-Thread. <c>ShowDialog</c> und <c>Dispatcher.Invoke</c> mit niedriger Priorität
/// öffnen dort verschachtelte Message-Loops, in denen die Dispatcher-Aufträge <em>anderer</em>, parallel laufender
/// Testklassen mit abgearbeitet werden — ein Dialog-Test kehrt dann erst zurück, wenn ein fremdes Layout oder ein fremder
/// Dialog fertig ist (Zeitgrenzen gerissen, Timeouts, einzeln aber stabil grün). <see cref="TestUiThread"/> prüft
/// deshalb, dass der aufrufende Test in dieser Sammlung steht.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiTestCollection
{
    public const string Name = "UI-Thread";
}
