using System.Windows;
using ProxyForge.Cache;
using ProxyForge.Encoding;
using ProxyForge.Export;
using ProxyForge.Sources;
using ProxyForge.Views;

namespace ProxyForge;

internal static class ProxyForgeHost
{
    static int started;

    public static ProxySourceProvider Provider { get; } = new(
        ProxyCache.Shared,
        ProxyGenerationQueue.Shared,
        ExportDetector.Shared.IsExporting,
        () => ProxyForgeSettings.Default,
        VideoSourceLoader.Load);

    public static void EnsureStartedOnce()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            return;

        ProxyForgeTelemetry.EnsureStartedOnce();
        if (ExportDetector.Shared.IsCommandLineEncode)
            return;

        ProxyForgeUpdateNotifier.EnsureCheckedOnce();
        var application = Application.Current;
        if (application is null)
            return;

        application.Dispatcher.InvokeAsync(() =>
        {
            ExportWindowWatcher.Attach(ExportDetector.Shared, ProxyForgeTelemetry.Report);
            GenerationProgressWindowHost.Attach(ProxyGenerationQueue.Shared, ProxyForgeSettings.Default);
            application.Exit += OnExit;
        });
    }

    static void OnExit(object sender, ExitEventArgs e)
    {
        ProxyGenerationQueue.Shared.CancelAll();
        GenerationProgressWindowHost.Shutdown();
        ExportWindowWatcher.Detach();
    }
}
