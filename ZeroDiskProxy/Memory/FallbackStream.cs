using System.Diagnostics;
using System.IO;

namespace ZeroDiskProxy.Memory;

internal sealed class FallbackStream : Stream
{
    private Stream _inner;
    private readonly MemoryBudget _budget;
    private readonly string _fallbackDir;
    private string? _fallbackPath;
    private volatile bool _isFallenBack;
    private int _disposed;
    private readonly object _switchLock = new();

    internal bool IsFallenBack => _isFallenBack;

    internal FallbackStream(MemoryBudget budget, string fallbackDirectory, int initialCapacity = 4096)
    {
        _budget = budget;
        _fallbackDir = fallbackDirectory;
        _inner = new MemoryStream(initialCapacity);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => !IsDisposed && _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!_isFallenBack && !_budget.CanAllocateInMemory(_inner.Position + count))
            SwitchToDisk();
        _inner.Write(buffer, offset, count);
    }

    internal byte[] ToArray()
    {
        lock (_switchLock)
        {
            if (_inner is MemoryStream ms)
                return ms.ToArray();

            var pos = _inner.Position;
            _inner.Position = 0;
            var result = new byte[_inner.Length];
            int totalRead = 0;
            while (totalRead < result.Length)
            {
                int read = _inner.Read(result, totalRead, result.Length - totalRead);
                if (read == 0)
                    break;
                totalRead += read;
            }
            _inner.Position = pos;
            return result;
        }
    }

    internal string? TransferDiskOwnership()
    {
        lock (_switchLock)
        {
            if (!_isFallenBack || _fallbackPath is null)
                return null;

            var path = _fallbackPath;
            _fallbackPath = null;
            return path;
        }
    }

    private void SwitchToDisk()
    {
        lock (_switchLock)
        {
            if (_isFallenBack)
                return;

            try
            {
                Directory.CreateDirectory(_fallbackDir);
                var path = Path.Combine(_fallbackDir, string.Concat("zdp_", Guid.NewGuid().ToString("N"), ".tmp"));
                var fileStream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 65536);

                var oldStream = _inner;
                var position = oldStream.Position;
                oldStream.Position = 0;
                oldStream.CopyTo(fileStream);
                fileStream.Position = position;

                var memLength = oldStream.Length;
                _inner = fileStream;
                _fallbackPath = path;
                _isFallenBack = true;

                oldStream.Dispose();
                _budget.RecordDeallocation(memLength);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Concat("[FallbackStream] Disk fallback failed: ", ex.Message));
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (disposing)
        {
            lock (_switchLock)
            {
                var length = _inner.Length;
                _inner.Dispose();

                if (!_isFallenBack)
                    _budget.RecordDeallocation(length);

                if (_fallbackPath is not null)
                {
                    try { File.Delete(_fallbackPath); }
                    catch { }
                    _fallbackPath = null;
                }
            }
        }

        base.Dispose(disposing);
    }
}
