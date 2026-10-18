using System;
using System.Windows;

namespace PhotoViewerV3.Shell;

/// <summary>Kleine Hilfe für Messenger-Empfänger: Aktion auf dem UI-Thread ausführen (Nachrichten kommen von Worker-Threads).</summary>
public static class UiThread
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }
}
