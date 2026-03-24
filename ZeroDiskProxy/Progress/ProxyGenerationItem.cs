using YukkuriMovieMaker.Commons;

namespace ZeroDiskProxy.Progress;

public sealed class ProxyGenerationItem : Bindable
{
    public string FileName { get; }
    public string OriginalPath { get; }
    public DateTime StartedAt { get; } = DateTime.Now;

    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            if (Set(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ElapsedText));
            }
        }
    }

    public string ProgressText => string.Concat(Progress.ToString("F1"), "%");

    public string ElapsedText
    {
        get
        {
            var elapsed = DateTime.Now - StartedAt;
            if (elapsed.TotalMinutes >= 1)
                return string.Concat(((int)elapsed.TotalMinutes).ToString(), "m", elapsed.Seconds.ToString(), "s");
            return string.Concat(elapsed.Seconds.ToString(), "s");
        }
    }

    private bool _isCompleted;
    public bool IsCompleted
    {
        get => _isCompleted;
        set => Set(ref _isCompleted, value);
    }

    private bool _isFailed;
    public bool IsFailed
    {
        get => _isFailed;
        set => Set(ref _isFailed, value);
    }

    private string _statusMessage = "生成中...";
    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    private bool _isInMemory = true;
    public bool IsInMemory
    {
        get => _isInMemory;
        set
        {
            if (Set(ref _isInMemory, value))
                OnPropertyChanged(nameof(StorageText));
        }
    }

    public string StorageText => _isInMemory ? "メモリ" : "ディスク";

    internal ProxyGenerationItem(string originalPath)
    {
        OriginalPath = originalPath;
        FileName = System.IO.Path.GetFileName(originalPath);
    }
}
