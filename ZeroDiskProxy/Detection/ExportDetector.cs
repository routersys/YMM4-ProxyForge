using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using YukkuriMovieMaker.Resources.Localization;
using ZeroDiskProxy.Interfaces;

namespace ZeroDiskProxy.Detection;

internal sealed partial class ExportDetector : IExportDetector
{
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, nint lParam);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

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
            var currentProcessId = (uint)Environment.ProcessId;
            bool found = false;

            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var windowPid);
                if (windowPid != currentProcessId)
                    return true;

                StringBuilder buff = new(256);
                int length = GetWindowText(hWnd, buff, 256);
                if (length <= 0)
                    return true;

                var title = buff.ToString();
                if (title == Texts.OutputProgressWindowTitle || title == Texts.VideoExportWindowTitle)
                {
                    found = true;
                    return false;
                }
                return true;
            }, nint.Zero);

            return found;
        }
        catch
        {
            return false;
        }
    }

    public void Reset()
    {
        Volatile.Write(ref _lastExportState, -1);
    }
}