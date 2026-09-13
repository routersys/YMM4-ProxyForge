using System.IO;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Encoding;

public enum ProxyGenerationStatus
{
    Waiting,
    Generating,
    Completed,
    Cancelled,
    Failed,
}

internal sealed class ProxyGenerationItem(string sourcePath, int scale) : Bindable
{
    double progress;
    ProxyGenerationStatus status;
    ProxyEncodeFailure? failure;

    public string SourcePath { get; } = sourcePath;

    public string FileName { get; } = Path.GetFileName(sourcePath);

    public int Scale { get; } = scale;

    public double Progress
    {
        get => progress;
        set => Set(ref progress, value);
    }

    public ProxyGenerationStatus Status
    {
        get => status;
        set => Set(ref status, value);
    }

    public ProxyEncodeFailure? Failure
    {
        get => failure;
        set => Set(ref failure, value);
    }
}
