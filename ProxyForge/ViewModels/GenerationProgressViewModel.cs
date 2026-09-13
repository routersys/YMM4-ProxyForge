using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using ProxyForge.Encoding;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.ViewModels;

internal sealed class GenerationProgressViewModel : Bindable
{
    readonly ObservableCollection<ProxyGenerationItem> source;

    public GenerationProgressViewModel(ObservableCollection<ProxyGenerationItem> source)
    {
        this.source = source;
        CollectionChangedEventManager.AddHandler(source, OnSourceChanged);
        Rebuild();
    }

    public ObservableCollection<GenerationItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                var index = e.NewStartingIndex < 0 ? Items.Count : e.NewStartingIndex;
                foreach (ProxyGenerationItem item in e.NewItems)
                    Items.Insert(index++, new GenerationItemViewModel(item));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (ProxyGenerationItem item in e.OldItems)
                {
                    var existing = Items.FirstOrDefault(candidate => ReferenceEquals(candidate.Item, item));
                    if (existing is not null)
                        Items.Remove(existing);
                }
                break;
            default:
                Rebuild();
                break;
        }

        OnPropertyChanged(nameof(HasItems));
    }

    void Rebuild()
    {
        Items.Clear();
        foreach (var item in source)
            Items.Add(new GenerationItemViewModel(item));
    }
}
