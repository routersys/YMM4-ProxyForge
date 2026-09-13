using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ProxyForge.Sources;

internal sealed class ProxyVideoSource : IVideoFileSource
{
    readonly Lock gate = new();
    readonly AffineTransform2D transform;
    readonly ID2D1Image output;
    readonly TimeSpan duration;
    IVideoFileSource inner;
    IGraphicsDevicesAndContext? innerContext;
    FrameRate? frameRate;
    ProxyUpgrade? pending;
    IDisposable? loader;
    bool disposed;

    ProxyVideoSource(IGraphicsDevicesAndContext devices, IVideoFileSource inner, TimeSpan duration, Matrix3x2 matrix, FrameRate? frameRate)
    {
        this.inner = inner;
        this.duration = duration;
        this.frameRate = frameRate;
        transform = new AffineTransform2D(devices.DeviceContext)
        {
            InterPolationMode = AffineTransform2DInterpolationMode.Cubic,
            BorderMode = BorderMode.Hard,
            TransformMatrix = matrix,
        };
        transform.SetInput(0, inner.Output, true);
        output = transform.Output;
    }

    public static ProxyVideoSource FromOriginal(IGraphicsDevicesAndContext devices, IVideoFileSource original)
        => new(devices, original, original.Duration, Matrix3x2.Identity, null);

    public static ProxyVideoSource FromProxy(IGraphicsDevicesAndContext devices, OpenedProxy proxy, TimeSpan duration)
        => new(devices, proxy.Source, duration, proxy.Transform, proxy.FrameRate);

    public TimeSpan Duration => duration;

    public ID2D1Image Output => output;

    public bool IsProxy
    {
        get
        {
            using (gate.EnterScope())
                return frameRate is not null;
        }
    }

    public void AttachLoader(IDisposable loader)
    {
        using (gate.EnterScope())
        {
            if (disposed)
            {
                loader.Dispose();
                return;
            }

            this.loader = loader;
        }
    }

    public bool TryHandOver(ProxyUpgrade upgrade)
    {
        using (gate.EnterScope())
        {
            if (disposed || frameRate is not null || pending is not null)
                return false;

            pending = upgrade;
            return true;
        }
    }

    public void Update(TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        ProxyUpgrade? upgrade;
        using (gate.EnterScope())
        {
            upgrade = pending;
            pending = null;
        }

        if (upgrade is not null)
            Apply(upgrade);

        inner.Update(time);
    }

    public int GetFrameIndex(TimeSpan time)
    {
        FrameRate? rate;
        using (gate.EnterScope())
            rate = frameRate;

        return rate is { } known ? known.GetFrameIndex(time) : inner.GetFrameIndex(time);
    }

    void Apply(ProxyUpgrade upgrade)
    {
        var previous = inner;
        transform.SetInput(0, upgrade.Proxy.Source.Output, true);
        transform.TransformMatrix = upgrade.Proxy.Transform;
        inner = upgrade.Proxy.Source;
        innerContext = upgrade.Context;
        previous.Dispose();

        IDisposable? attached;
        using (gate.EnterScope())
        {
            frameRate = upgrade.Proxy.FrameRate;
            attached = loader;
            loader = null;
        }

        attached?.Dispose();
    }

    public void Dispose()
    {
        ProxyUpgrade? orphan;
        IDisposable? attached;
        using (gate.EnterScope())
        {
            if (disposed)
                return;

            disposed = true;
            orphan = pending;
            pending = null;
            attached = loader;
            loader = null;
        }

        attached?.Dispose();
        orphan?.Dispose();
        output.Dispose();
        transform.SetInput(0, null, true);
        transform.Dispose();
        inner.Dispose();
        innerContext?.Dispose();
    }
}
