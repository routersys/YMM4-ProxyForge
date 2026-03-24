using System.Collections.ObjectModel;
using YukkuriMovieMaker.Commons;
using ZeroDiskProxy.Progress;

namespace ZeroDiskProxy.ViewModels;

internal sealed class GenerationPopupViewModel : Bindable
{
    public ObservableCollection<ProxyGenerationItem> Items { get; }

    private bool _hasItems;
    public bool HasItems
    {
        get => _hasItems;
        set => Set(ref _hasItems, value);
    }

    internal GenerationPopupViewModel(ObservableCollection<ProxyGenerationItem> items)
    {
        Items = items;
        Items.CollectionChanged += (_, _) => HasItems = Items.Count > 0;
        HasItems = Items.Count > 0;
    }
}