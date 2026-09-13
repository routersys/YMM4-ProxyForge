using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Export;

internal static unsafe partial class ExportWindowWatcher
{
    const uint EventObjectDestroy = 0x8001;
    const uint EventObjectShow = 0x8002;
    const uint EventObjectHide = 0x8003;
    const uint EventObjectNameChange = 0x800C;
    const uint OutOfContext = 0;
    const uint AncestorParent = 1;
    const int TitleCapacity = 256;
    static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);

    static readonly Dictionary<nint, ExportWindowRole> windows = [];
    static ExportDetector? detector;
    static Action<Exception>? reportUnexpected;
    static Dispatcher? dispatcher;
    static nint visibilityHook;
    static nint nameHook;
    static uint threadId;
    static ExportPhase scanned;

    public static bool IsAttached => visibilityHook != 0;

    public static void Attach(ExportDetector detector, Action<Exception> reportUnexpected)
    {
        if (IsAttached)
            return;

        ExportWindowWatcher.detector = detector;
        ExportWindowWatcher.reportUnexpected = reportUnexpected;
        threadId = GetCurrentThreadId();
        var process = (uint)Environment.ProcessId;
        visibilityHook = SetWinEventHook(EventObjectDestroy, EventObjectHide, 0, &OnWinEvent, process, threadId, OutOfContext);
        nameHook = SetWinEventHook(EventObjectNameChange, EventObjectNameChange, 0, &OnWinEvent, process, threadId, OutOfContext);
        if (visibilityHook == 0 || nameHook == 0)
        {
            Log.Default.Write("ProxyForge: 出力ウィンドウの監視を開始できませんでした。");
            Detach();
            return;
        }

        Volatile.Write(ref dispatcher, Dispatcher.CurrentDispatcher);
    }

    public static void Detach()
    {
        Volatile.Write(ref dispatcher, null);
        if (visibilityHook != 0)
            UnhookWinEvent(visibilityHook);
        if (nameHook != 0)
            UnhookWinEvent(nameHook);

        visibilityHook = 0;
        nameHook = 0;
        windows.Clear();
        detector = null;
        reportUnexpected = null;
    }

    public static ExportPhase? ResolvePhase()
    {
        var target = Volatile.Read(ref dispatcher);
        if (target is null || target.HasShutdownStarted)
            return null;
        if (target.CheckAccess())
            return Scan();

        try
        {
            var operation = target.InvokeAsync(Scan, DispatcherPriority.ContextIdle);
            if (operation.Task.Wait(ResolveTimeout))
                return operation.Result;

            operation.Abort();
            return null;
        }
        catch (Exception exception) when (exception is TaskCanceledException or AggregateException or InvalidOperationException)
        {
            return null;
        }
    }

    public static ExportPhase? Scan()
    {
        if (detector is null)
            return null;

        scanned = ComponentDispatcher.IsThreadModal ? ExportPhase.Preparing : ExportPhase.Idle;
        EnumThreadWindows(threadId, &OnWindowEnumerated, 0);
        return scanned;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (objectId != 0 || childId != 0 || window == 0 || detector is not { } current)
            return;

        try
        {
            Handle(current, eventType, window);
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int OnWindowEnumerated(nint window, nint parameter)
    {
        if (detector is not { } current)
            return 0;

        try
        {
            return Inspect(current, window) ? 1 : 0;
        }
        catch (Exception exception)
        {
            Report(exception);
            return 0;
        }
    }

    static void Handle(ExportDetector current, uint eventType, nint window)
    {
        switch (eventType)
        {
            case EventObjectShow:
            case EventObjectNameChange:
                if (!windows.ContainsKey(window) && IsWindowVisible(window) && GetAncestor(window, AncestorParent) == GetDesktopWindow())
                    Track(current, window);
                break;
            case EventObjectHide:
            case EventObjectDestroy:
                if (windows.Remove(window, out var role))
                    current.OnWindowClosed(role);
                break;
        }
    }

    static void Track(ExportDetector current, nint window)
    {
        Span<char> title = stackalloc char[TitleCapacity];
        var role = current.Classify(ReadTitle(window, title));
        if (role == ExportWindowRole.None)
            return;

        windows[window] = role;
        current.OnWindowOpened(role);
    }

    static bool Inspect(ExportDetector current, nint window)
    {
        if (!IsWindowVisible(window))
            return true;

        Span<char> title = stackalloc char[TitleCapacity];
        switch (current.Classify(ReadTitle(window, title)))
        {
            case ExportWindowRole.Progress:
                scanned = ExportPhase.Exporting;
                return false;
            case ExportWindowRole.Configuration:
                scanned = ExportPhase.Preparing;
                return true;
        }

        if (!IsWindowEnabled(window))
            scanned = ExportPhase.Preparing;
        return true;
    }

    static ReadOnlySpan<char> ReadTitle(nint window, Span<char> buffer)
    {
        fixed (char* pointer = buffer)
        {
            var length = GetWindowText(window, pointer, buffer.Length);
            return buffer[..Math.Max(length, 0)];
        }
    }

    static void Report(Exception exception)
    {
        Log.Default.Write("ProxyForge: 出力ウィンドウの監視で想定していない例外が起きました。", exception);
        reportUnexpected?.Invoke(exception);
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module, delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> procedure, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumThreadWindows(uint threadId, delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowEnabled(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static partial int GetWindowText(nint window, char* buffer, int capacity);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
