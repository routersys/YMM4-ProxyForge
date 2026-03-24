using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ZeroDiskProxy.Interop;

internal static class MfRuntime
{
    private static int _refCount;
    private static readonly object SyncRoot = new();

    internal static void Startup()
    {
        lock (SyncRoot)
        {
            if (_refCount == 0)
                HResultExtensions.ThrowOnFailure(MfNativeMethods.MFStartup(MfNativeMethods.MF_VERSION), "MFStartup");
            _refCount++;
        }
    }

    internal static void Shutdown()
    {
        lock (SyncRoot)
        {
            if (_refCount <= 0)
                return;
            _refCount--;
            if (_refCount == 0)
                _ = MfNativeMethods.MFShutdown();
        }
    }
}

internal sealed class ManagedIStream : IStream
{
    private readonly Stream _stream;

    internal ManagedIStream(Stream stream)
    {
        _stream = stream;
    }

    public void Read(byte[] pv, int cb, nint pcbRead)
    {
        int bytesRead = _stream.Read(pv, 0, cb);
        if (pcbRead != nint.Zero)
            Marshal.WriteInt32(pcbRead, bytesRead);
    }

    public void Write(byte[] pv, int cb, nint pcbWritten)
    {
        _stream.Write(pv, 0, cb);
        if (pcbWritten != nint.Zero)
            Marshal.WriteInt32(pcbWritten, cb);
    }

    public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
    {
        long newPos = _stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
        if (plibNewPosition != nint.Zero)
            Marshal.WriteInt64(plibNewPosition, newPos);
    }

    public void SetSize(long libNewSize)
    {
        _stream.SetLength(libNewSize);
    }

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            cbSize = _stream.Length,
            type = 2,
            grfMode = 2
        };
    }

    public void CopyTo(IStream pstm, long cb, nint pcbRead, nint pcbWritten) => throw new NotImplementedException();
    public void Commit(int grfCommitFlags) => _stream.Flush();
    public void Revert() { }
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotImplementedException();
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotImplementedException();
    public void Clone(out IStream ppstm) => throw new NotImplementedException();
}