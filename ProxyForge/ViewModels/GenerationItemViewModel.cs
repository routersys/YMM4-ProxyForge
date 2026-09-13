using System.ComponentModel;
using System.Windows;
using ProxyForge.Encoding;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.ViewModels;

internal sealed class GenerationItemViewModel : Bindable
{
    public GenerationItemViewModel(ProxyGenerationItem item)
    {
        Item = item;
        PropertyChangedEventManager.AddHandler(item, OnItemChanged, string.Empty);
    }

    public ProxyGenerationItem Item { get; }

    public string FileName => Item.FileName;

    public string SourcePath => Item.SourcePath;

    public double Percentage => Item.Progress * 100d;

    public bool IsWaiting => Item.Status == ProxyGenerationStatus.Waiting;

    public string StatusText => Item.Status switch
    {
        ProxyGenerationStatus.Waiting => Texts.StatusWaiting,
        ProxyGenerationStatus.Generating => Texts.StatusGenerating,
        ProxyGenerationStatus.Completed => Texts.StatusCompleted,
        ProxyGenerationStatus.Cancelled => Texts.StatusCancelled,
        _ => Item.Failure is { } failure ? string.Concat(Texts.StatusFailed, ": ", DescribeFailure(failure)) : Texts.StatusFailed,
    };

    public static string DescribeFailure(ProxyEncodeFailure failure) => failure switch
    {
        ProxyEncodeFailure.FFmpegUnavailable => Texts.FailureFFmpegUnavailable,
        ProxyEncodeFailure.SourceUnavailable => Texts.FailureSourceUnavailable,
        ProxyEncodeFailure.NoFrames => Texts.FailureNoFrames,
        ProxyEncodeFailure.UnusableSize => Texts.FailureUnusableSize,
        ProxyEncodeFailure.Transparent => Texts.FailureTransparent,
        ProxyEncodeFailure.FFmpegFailed => Texts.FailureFFmpegFailed,
        ProxyEncodeFailure.NoOutput => Texts.FailureNoOutput,
        ProxyEncodeFailure.SourceChanged => Texts.FailureSourceChanged,
        _ => failure.ToString(),
    };

    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProxyGenerationItem.Progress):
                OnPropertyChanged(nameof(Percentage));
                break;
            case nameof(ProxyGenerationItem.Status):
            case nameof(ProxyGenerationItem.Failure):
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsWaiting));
                break;
        }
    }
}
