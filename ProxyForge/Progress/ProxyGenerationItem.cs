using ProxyForge.Localization;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Progress;

public sealed class ProxyGenerationItem : Bindable
{
    public string FileName { get; }
    public string OriginalPath { get; }
    public DateTime StartedAt { get; } = DateTime.Now;

    private long _pendingProgressBits;

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

    public string ProgressText => string.Create(null, stackalloc char[16], $"{Progress:F1}%");

    public string ElapsedText
    {
        get
        {
            var elapsed = DateTime.Now - StartedAt;
            return elapsed.TotalMinutes >= 1
                ? string.Create(null, stackalloc char[16], $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds}s")
                : string.Create(null, stackalloc char[8], $"{elapsed.Seconds}s");
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

    private string _statusMessage = Translate.ProxyGenerationStatusGenerating;
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

    public string StorageText => _isInMemory ? Translate.StorageMemory : Translate.StorageDisk;

    internal ProxyGenerationItem(string originalPath)
    {
        OriginalPath = originalPath;
        FileName = System.IO.Path.GetFileName(originalPath);
    }

    internal void SetPendingProgress(double value) =>
        Interlocked.Exchange(ref _pendingProgressBits, BitConverter.DoubleToInt64Bits(value));

    internal void ApplyPendingProgress() =>
        Progress = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _pendingProgressBits));
}