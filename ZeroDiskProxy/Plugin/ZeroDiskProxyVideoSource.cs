using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ZeroDiskProxy.Plugin;

internal sealed class ZeroDiskProxyVideoSource : IVideoFileSource
{
    private IVideoFileSource _inner;
    private readonly Func<IVideoFileSource?>? _tryUpgradeSource;
    private readonly AffineTransform2D _switchEffect;
    private readonly ID2D1Image _output;
    private readonly List<IVideoFileSource> _retiredSources = [];
    private readonly object _gate = new();
    private volatile bool _upgraded;
    private int _disposed;

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
                return _inner.Duration;
        }
    }

    public ID2D1Image Output => _output;

    internal ZeroDiskProxyVideoSource(IVideoFileSource inner, IGraphicsDevicesAndContext devices)
    {
        _inner = inner;
        _switchEffect = new AffineTransform2D(devices.DeviceContext);
        _switchEffect.SetInput(0, inner.Output, true);
        _switchEffect.TransformMatrix = Matrix3x2.Identity;
        _output = _switchEffect.Output;
    }

    internal ZeroDiskProxyVideoSource(IVideoFileSource inner, IGraphicsDevicesAndContext devices, Func<IVideoFileSource?> tryUpgradeSource)
    {
        _inner = inner;
        _switchEffect = new AffineTransform2D(devices.DeviceContext);
        _switchEffect.SetInput(0, inner.Output, true);
        _switchEffect.TransformMatrix = Matrix3x2.Identity;
        _output = _switchEffect.Output;
        _tryUpgradeSource = tryUpgradeSource;
    }

    public void Update(TimeSpan time)
    {
        if (!_upgraded && _tryUpgradeSource is not null)
        {
            try
            {
                var upgraded = _tryUpgradeSource();
                if (upgraded is not null)
                {
                    lock (_gate)
                    {
                        if (!_upgraded && Volatile.Read(ref _disposed) == 0)
                        {
                            _retiredSources.Add(_inner);
                            _inner = upgraded;
                            _switchEffect.SetInput(0, upgraded.Output, true);
                            _upgraded = true;
                        }
                        else
                        {
                            upgraded.Dispose();
                        }
                    }
                }
            }
            catch { }
        }

        lock (_gate)
            _inner.Update(time);
    }

    public int GetFrameIndex(TimeSpan time)
    {
        lock (_gate)
            return _inner.GetFrameIndex(time);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_gate)
        {
            _output.Dispose();
            _switchEffect.SetInput(0, null, true);
            _switchEffect.Dispose();
            _inner.Dispose();

            foreach (var source in _retiredSources)
                source.Dispose();

            _retiredSources.Clear();
        }

        GC.SuppressFinalize(this);
    }
}
