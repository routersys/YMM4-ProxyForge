using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;

namespace ZeroDiskProxy.Plugin;

internal sealed class ZeroDiskProxyVideoSource : IVideoFileSource
{
    private IVideoFileSource _inner;
    private Func<IVideoFileSource?>? _tryUpgradeSource;
    private readonly AffineTransform2D _switchEffect;
    private readonly ID2D1Image _output;
    private List<IVideoFileSource>? _retiredSources;
    private readonly object _gate = new();
    private volatile bool _upgraded;
    private volatile IVideoFileSource? _pendingUpgrade;
    private int _upgradeFetching;
    private long _nextUpgradeAttemptTicks;
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
            TryApplyUpgrade();

        if (_upgraded)
        {
            if (Volatile.Read(ref _disposed) == 0)
                _inner.Update(time);
            return;
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 0)
                _inner.Update(time);
        }
    }

    private void TryApplyUpgrade()
    {
        var pending = _pendingUpgrade;
        if (pending is not null)
        {
            _pendingUpgrade = null;
            lock (_gate)
            {
                if (!_upgraded && Volatile.Read(ref _disposed) == 0)
                {
                    var old = _inner;
                    _inner = pending;
                    _switchEffect.SetInput(0, pending.Output, true);
                    _upgraded = true;
                    _tryUpgradeSource = null;
                    _retiredSources = null;
                    old.Dispose();
                    ThreadPool.QueueUserWorkItem(static _ => ForceMemoryReclaim());
                }
                else
                {
                    pending.Dispose();
                }
            }
        }
        else
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= Volatile.Read(ref _nextUpgradeAttemptTicks) &&
                Interlocked.Exchange(ref _upgradeFetching, 1) == 0)
            {
                Volatile.Write(ref _nextUpgradeAttemptTicks, now + Stopwatch.Frequency / 2);
                ThreadPool.UnsafeQueueUserWorkItem(static s => s.FetchUpgradeAsync(), this, preferLocal: false);
            }
        }
    }

    private void FetchUpgradeAsync()
    {
        try
        {
            var upgraded = _tryUpgradeSource!();
            if (upgraded is not null)
                _pendingUpgrade = upgraded;
        }
        catch { }
        finally
        {
            Volatile.Write(ref _upgradeFetching, 0);
        }
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

            if (_retiredSources is { } retired)
            {
                foreach (var source in retired)
                    source.Dispose();
                retired.Clear();
            }
        }

        var orphan = _pendingUpgrade;
        if (orphan is not null)
        {
            _pendingUpgrade = null;
            orphan.Dispose();
        }

        GC.SuppressFinalize(this);
    }
    private static void ForceMemoryReclaim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(1, GCCollectionMode.Forced, true);

            try
            {
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch { }
        }
        catch { }
    }

    [DllImport("kernel32")]
    private static extern nint GetCurrentProcess();

    [DllImport("psapi")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint hProcess);
}