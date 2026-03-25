using ProxyForge.Interfaces;
using System.Diagnostics;
using System.Runtime.InteropServices;
using YukkuriMovieMaker.Resources.Localization;

namespace ProxyForge.Detection;

internal sealed partial class ExportDetector : IExportDetector
{
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, nint lParam);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [ThreadStatic]
    private static bool t_found;
    [ThreadStatic]
    private static uint t_processId;
    [ThreadStatic]
    private static char[]? t_titleBuffer;

    private const int WindowTitleBufferSize = 512;
    private const long CacheIntervalMs = 2000L;
    private const long StateUnknown = 0L;
    private const long StateFalse = 1L;
    private const long StateTrue = 2L;

    private static readonly EnumWindowsProc s_enumWindowsCallback = EnumWindowsCallback;

    private long _packed;

    public bool IsExporting()
    {
        var nowMs = Environment.TickCount64;
        var packed = Interlocked.Read(ref _packed);
        var state = packed & 3L;

        if (state != StateUnknown && (nowMs - (packed >> 2)) < CacheIntervalMs)
            return state == StateTrue;

        var result = CheckExportWindow();
        Interlocked.Exchange(ref _packed, (nowMs << 2) | (result ? StateTrue : StateFalse));
        return result;
    }

    private static bool CheckExportWindow()
    {
        Debug.Assert(
            SynchronizationContext.Current is null || !Thread.CurrentThread.IsThreadPoolThread,
            "CheckExportWindow relies on ThreadStatic fields and must not be called from async continuations on thread pool threads.");

        try
        {
            t_processId = (uint)Environment.ProcessId;
            t_found = false;
            EnumWindows(s_enumWindowsCallback, nint.Zero);
            return t_found;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnumWindowsCallback(nint hWnd, nint _)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid != t_processId)
            return true;

        t_titleBuffer ??= new char[WindowTitleBufferSize];
        var len = GetWindowText(hWnd, t_titleBuffer, t_titleBuffer.Length);
        if (len <= 0)
            return true;

        var title = t_titleBuffer.AsSpan(0, len);
        if (title.SequenceEqual(Texts.OutputProgressWindowTitle.AsSpan()) ||
            title.SequenceEqual(Texts.VideoExportWindowTitle.AsSpan()))
        {
            t_found = true;
            return false;
        }
        return true;
    }

    public void Reset() => Interlocked.Exchange(ref _packed, 0L);
}