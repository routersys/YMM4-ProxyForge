using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace ProxyForge.Export;

internal static class ExportWindowWatcher
{
    static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);
    static readonly DependencyPropertyDescriptor TitleDescriptor =
        DependencyPropertyDescriptor.FromProperty(Window.TitleProperty, typeof(Window));

    static int attached;

    public static void Attach(ExportDetector detector)
    {
        if (Interlocked.Exchange(ref attached, 1) != 0)
            return;

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, arguments) =>
            {
                if (sender is Window window)
                    new Tracker(window, detector);
            }));
    }

    public static bool? ResolveOnUiThread(Func<bool> probe)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess())
            return null;

        try
        {
            var operation = dispatcher.InvokeAsync(probe, DispatcherPriority.ContextIdle);
            if (!operation.Task.Wait(ResolveTimeout))
                return null;

            return operation.Result;
        }
        catch (Exception exception) when (exception is TaskCanceledException or AggregateException or InvalidOperationException)
        {
            return null;
        }
    }

    sealed class Tracker
    {
        readonly Window window;
        readonly ExportDetector detector;
        ExportWindowRole role;

        public Tracker(Window window, ExportDetector detector)
        {
            this.window = window;
            this.detector = detector;
            window.Closed += OnClosed;
            TitleDescriptor.AddValueChanged(window, OnTitleChanged);
            Evaluate();
        }

        void OnTitleChanged(object? sender, EventArgs e) => Evaluate();

        void Evaluate()
        {
            if (role != ExportWindowRole.None)
                return;

            role = detector.Classify(window.Title);
            if (role != ExportWindowRole.None)
                detector.OnWindowOpened(role);
        }

        void OnClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnClosed;
            TitleDescriptor.RemoveValueChanged(window, OnTitleChanged);
            detector.OnWindowClosed(role);
        }
    }
}
