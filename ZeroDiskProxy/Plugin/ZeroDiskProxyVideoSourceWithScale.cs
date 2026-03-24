using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ZeroDiskProxy.Plugin;

internal sealed class ZeroDiskProxyVideoSourceWithScale : IVideoFileSource
{
    private readonly IVideoFileSource _inner;
    private readonly AffineTransform2D _scaleEffect;
    private readonly ID2D1Image _output;
    private int _disposed;

    public TimeSpan Duration => _inner.Duration;
    public ID2D1Image Output => _output;

    internal ZeroDiskProxyVideoSourceWithScale(IVideoFileSource inner, IGraphicsDevicesAndContext devices, float proxyScale)
    {
        _inner = inner;
        _scaleEffect = new AffineTransform2D(devices.DeviceContext);
        _scaleEffect.SetInput(0, inner.Output, true);
        _scaleEffect.TransformMatrix = Matrix3x2.CreateScale(1f / proxyScale);
        _output = _scaleEffect.Output;
    }

    public void Update(TimeSpan time) => _inner.Update(time);
    public int GetFrameIndex(TimeSpan time) => _inner.GetFrameIndex(time);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _output.Dispose();
        _scaleEffect.SetInput(0, null, true);
        _scaleEffect.Dispose();
        _inner.Dispose();
        GC.SuppressFinalize(this);
    }
}