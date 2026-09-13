using System.Numerics;
using ProxyForge.Cache;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Sources;

internal sealed class ProxyVideoSource : IVideoFileSource
{
    public const int OpenChunkRadius = 1;

    readonly IGraphicsDevicesAndContext devices;
    readonly SourceIdentity identity;
    readonly ProxyChunkLoader loader;
    readonly VideoSourceFactory factory;
    readonly SourceFocus focus;
    readonly AffineTransform2D transform;
    readonly ID2D1Image output;
    readonly Dictionary<int, OpenedChunk> chunks = [];
    IVideoFileSource? original;
    ProxyCacheEntry? layout;
    ID2D1Image? input;
    int? shownChunk;
    bool originalUnavailable;
    bool disposed;

    public ProxyVideoSource(
        IGraphicsDevicesAndContext devices,
        SourceIdentity identity,
        IVideoFileSource? original,
        ProxyCacheEntry? entry,
        ProxyChunkLoader loader,
        VideoSourceFactory factory,
        SourceFocus focus)
    {
        if (original is null && entry is null)
            throw new ArgumentException("Either the original source or a cache entry is required.", nameof(entry));

        this.devices = devices;
        this.identity = identity;
        this.original = original;
        this.loader = loader;
        this.factory = factory;
        this.focus = focus;
        layout = entry;
        transform = new AffineTransform2D(devices.DeviceContext)
        {
            InterPolationMode = AffineTransform2DInterpolationMode.Cubic,
            BorderMode = BorderMode.Hard,
            TransformMatrix = Matrix3x2.Identity,
        };
        output = transform.Output;
    }

    public TimeSpan Duration => layout is { } known ? new TimeSpan(known.DurationTicks) : original!.Duration;

    public ID2D1Image Output => output;

    public bool IsProxy => shownChunk is not null;

    public int? ShownChunk => shownChunk;

    public int OpenChunkCount => chunks.Count;

    public bool HasOriginal => original is not null;

    public int GetFrameIndex(TimeSpan time)
        => layout is { } known ? new FrameRate(known.FrameRateNumerator, known.FrameRateDenominator).GetFrameIndex(time) : original!.GetFrameIndex(time);

    public void Update(TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var entry = loader.Entry;
        if (entry is not null)
            layout = entry;

        var frameRate = layout is { } known ? new FrameRate(known.FrameRateNumerator, known.FrameRateDenominator) : default;
        var frame = layout is null ? original!.GetFrameIndex(time) : frameRate.GetContainingFrame(time);
        focus.Report(identity, frame);

        if (entry is null)
        {
            Evict(null);
            ShowOriginal(time);
            return;
        }

        var chunk = Math.Min(frame / entry.ChunkLength, entry.ChunkCount - 1);
        TakeLoaded();
        if (!chunks.ContainsKey(chunk) && entry.HasChunk(chunk))
        {
            if (original is null && !originalUnavailable)
            {
                var opened = loader.Open(devices, chunk);
                if (opened is not null)
                    chunks[chunk] = opened;
            }
            else
            {
                loader.Request(chunk);
            }
        }

        if (!chunks.ContainsKey(chunk + 1) && entry.HasChunk(chunk + 1))
            loader.Request(chunk + 1);

        if (!chunks.TryGetValue(chunk, out var current))
        {
            Evict(chunk);
            ShowOriginal(time);
            return;
        }

        current.Source.Update(time - frameRate.GetFrameStart(chunk * entry.ChunkLength));
        Show(current.Source.Output, current.Transform, chunk);
        Evict(chunk);
        if (entry.IsComplete)
            DisposeOriginal();
    }

    void ShowOriginal(TimeSpan time)
    {
        if (original is null && !originalUnavailable)
        {
            original = factory(devices, identity.Path);
            if (original is null)
            {
                originalUnavailable = true;
                Log.Default.Write($"ProxyForge: 元の動画を開けなかったため、プロキシの無い区間は描画できません。{identity.Path}");
            }
        }

        if (original is null)
        {
            Show(null, Matrix3x2.Identity, null);
            return;
        }

        original.Update(time);
        Show(original.Output, Matrix3x2.Identity, null);
    }

    void Show(ID2D1Image? image, Matrix3x2 matrix, int? chunk)
    {
        if (!ReferenceEquals(input, image))
        {
            transform.SetInput(0, image, true);
            input = image;
        }

        transform.TransformMatrix = matrix;
        shownChunk = chunk;
    }

    void TakeLoaded()
    {
        foreach (var (index, opened) in loader.TakeLoaded())
        {
            if (chunks.TryAdd(index, opened))
                continue;

            opened.Dispose();
        }
    }

    void Evict(int? keep)
    {
        foreach (var (index, opened) in chunks.ToArray())
        {
            if (keep is { } center && Math.Abs(index - center) <= OpenChunkRadius)
                continue;

            chunks.Remove(index);
            if (ReferenceEquals(input, opened.Source.Output))
                Show(null, Matrix3x2.Identity, null);
            opened.Dispose();
        }
    }

    void DisposeOriginal()
    {
        if (original is null || ReferenceEquals(input, original.Output))
            return;

        original.Dispose();
        original = null;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        loader.Dispose();
        output.Dispose();
        transform.SetInput(0, null, true);
        transform.Dispose();
        foreach (var opened in chunks.Values)
            opened.Dispose();
        chunks.Clear();
        original?.Dispose();
        original = null;
    }
}
