using System.Windows;

namespace ProxyForge;

internal static class UiThread
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted)
            return;

        dispatcher.InvokeAsync(action);
    }
}
