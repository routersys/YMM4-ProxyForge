using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using ProxyForge.Encoding;
using ProxyForge.ViewModels;

namespace ProxyForge.Views;

internal static class GenerationProgressWindowHost
{
    static ObservableCollection<ProxyGenerationItem>? items;
    static Action? cancelAll;
    static ProxyForgeSettings? settings;
    static GenerationProgressWindow? window;
    static bool suppressed;

    public static bool ShouldShow(int itemCount, bool enabled, bool hiddenByUser)
        => itemCount > 0 && enabled && !hiddenByUser;

    public static void Attach(ProxyGenerationQueue queue, ProxyForgeSettings current)
    {
        if (items is not null)
            return;

        items = queue.Items;
        cancelAll = queue.CancelAll;
        settings = current;
        CollectionChangedEventManager.AddHandler(items, OnItemsChanged);
        PropertyChangedEventManager.AddHandler(current, OnSettingsChanged, nameof(ProxyForgeSettings.ShowsProgressWindow));
        Update();
    }

    public static void Shutdown()
    {
        window?.CloseForShutdown();
        window = null;
    }

    static void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Update();

    static void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Update();

    static void Update()
    {
        if (items is null || settings is null)
            return;

        if (items.Count == 0)
            suppressed = false;

        if (!ShouldShow(items.Count, settings.ShowsProgressWindow, suppressed))
        {
            window?.Hide();
            return;
        }

        window ??= Create();
        if (!window.IsVisible)
            window.Show();
    }

    static GenerationProgressWindow Create()
    {
        var created = new GenerationProgressWindow(new GenerationProgressViewModel(items!, cancelAll!));
        if (Application.Current?.MainWindow is { IsLoaded: true } owner)
            created.Owner = owner;
        created.HideRequested += (_, _) => suppressed = true;
        return created;
    }
}
