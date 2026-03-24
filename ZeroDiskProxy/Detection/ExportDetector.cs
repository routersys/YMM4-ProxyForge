using System.Diagnostics;
using System.Runtime.InteropServices;
using YukkuriMovieMaker.Resources.Localization;
using ZeroDiskProxy.Interfaces;

namespace ZeroDiskProxy.Detection;

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

    private static readonly EnumWindowsProc s_enumWindowsCallback = EnumWindowsCallback;

    private int _lastExportState = -1;
    private long _lastCheckTicks;
    private const long CacheIntervalTicks = 2000 * TimeSpan.TicksPerMillisecond;

    public bool IsExporting()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastCheckTicks);
        var elapsedTicks = (now - last) * TimeSpan.TicksPerSecond / Stopwatch.Frequency;

        if (elapsedTicks < CacheIntervalTicks)
        {
            var cached = Volatile.Read(ref _lastExportState);
            if (cached >= 0)
                return cached != 0;
        }

        Volatile.Write(ref _lastCheckTicks, now);
        var result = CheckExportWindow();
        Volatile.Write(ref _lastExportState, result ? 1 : 0);
        return result;
    }

    private static bool CheckExportWindow()
    {
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

        t_titleBuffer ??= new char[256];
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

    public void Reset() => Volatile.Write(ref _lastExportState, -1);
}