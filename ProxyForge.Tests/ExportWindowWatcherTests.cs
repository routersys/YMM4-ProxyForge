using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ProxyForge.Export;

namespace ProxyForge.Tests;

[Collection("Wpf")]
public sealed class ExportWindowWatcherTests
{
    const string Configuration = "動画出力";
    const string Progress = "出力";
    const int ChildVisibleStyle = 0x50000000;

    static readonly ExportDetector Detector = new(false, () => Configuration, () => Progress, () => null);

    static T RunSta<T>(Func<T> action)
    {
        var result = default(T)!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            throw new InvalidOperationException(error.Message, error);
        return result;
    }

    static T Watch<T>(Func<T> action)
        => RunSta(() =>
        {
            var reported = new List<Exception>();
            ExportWindowWatcher.Attach(Detector, reported.Add);
            try
            {
                Assert.True(ExportWindowWatcher.IsAttached);
                return action();
            }
            finally
            {
                ExportWindowWatcher.Detach();
                Detector.OnWindowClosed(ExportWindowRole.Progress);
                Assert.Empty(reported);
                Assert.False(ExportWindowWatcher.IsAttached);
            }
        });

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.ContextIdle);
        Dispatcher.PushFrame(frame);
    }

    static void PumpUntil(Func<bool> condition)
    {
        while (!condition())
            Pump();
    }

    static Window CreateWindow(string title) => new()
    {
        Title = title,
        Width = 120,
        Height = 80,
        Left = -10_000,
        Top = -10_000,
        ShowInTaskbar = false,
        ShowActivated = false,
        WindowStyle = WindowStyle.None,
    };

    static (ExportPhase AfterShow, ExportPhase AfterClose) Observe(string initialTitle, string? lateTitle)
        => Watch(() =>
        {
            var window = CreateWindow(initialTitle);
            window.Show();
            Pump();
            if (lateTitle is not null)
            {
                window.Title = lateTitle;
                Pump();
            }

            var afterShow = Detector.Phase;
            window.Close();
            Pump();
            return (afterShow, Detector.Phase);
        });

    [Fact]
    public void AProgressWindowTitledBeforeShowingIsSeenAsExportingUntilItCloses()
    {
        var (afterShow, afterClose) = Observe(Progress, null);

        Assert.Equal(ExportPhase.Exporting, afterShow);
        Assert.Equal(ExportPhase.Idle, afterClose);
    }

    [Fact]
    public void AProgressWindowTitledAfterShowingIsStillSeen()
    {
        var (afterShow, afterClose) = Observe(string.Empty, Progress);

        Assert.Equal(ExportPhase.Exporting, afterShow);
        Assert.Equal(ExportPhase.Idle, afterClose);
    }

    [Fact]
    public void AConfigurationWindowStartsThePreparationAndClosingKeepsIt()
    {
        var (afterShow, afterClose) = Observe(Configuration, null);

        Assert.Equal(ExportPhase.Preparing, afterShow);
        Assert.Equal(ExportPhase.Preparing, afterClose);
    }

    [Fact]
    public void AnUnrelatedWindowChangesNothing()
    {
        var (afterShow, afterClose) = Observe("Something else", "Still something else");

        Assert.Equal(ExportPhase.Idle, afterShow);
        Assert.Equal(ExportPhase.Idle, afterClose);
    }

    [Fact]
    public void ARetitledProgressWindowStaysAnExportUntilItCloses()
    {
        var (afterShow, afterClose) = Observe(Progress, "Something else");

        Assert.Equal(ExportPhase.Exporting, afterShow);
        Assert.Equal(ExportPhase.Idle, afterClose);
    }

    [Fact]
    public void AProgressWindowRetitledAsTheConfigurationStillEndsTheExportWhenClosed()
    {
        var (afterShow, afterClose) = Observe(Progress, Configuration);

        Assert.Equal(ExportPhase.Exporting, afterShow);
        Assert.Equal(ExportPhase.Idle, afterClose);
    }

    [Fact]
    public void AHiddenWindowIsNotSeenUntilItIsShown()
    {
        var (whileHidden, afterShow, afterClose) = Watch(() =>
        {
            var window = CreateWindow("Something else");
            new WindowInteropHelper(window).EnsureHandle();
            window.Title = Progress;
            Pump();
            var whileHidden = Detector.Phase;
            window.Show();
            Pump();
            var afterShow = Detector.Phase;
            window.Close();
            Pump();
            return (whileHidden, afterShow, Detector.Phase);
        });

        Assert.Equal(ExportPhase.Idle, whileHidden);
        Assert.Equal(ExportPhase.Exporting, afterShow);
        Assert.Equal(ExportPhase.Idle, afterClose);
    }

    [Fact]
    public void AHiddenProgressWindowEndsTheExportAndShowingItAgainResumes()
    {
        var (afterHide, afterShowAgain) = Watch(() =>
        {
            var window = CreateWindow(Progress);
            window.Show();
            Pump();
            window.Hide();
            Pump();
            var afterHide = Detector.Phase;
            window.Show();
            Pump();
            var afterShowAgain = Detector.Phase;
            window.Close();
            Pump();
            return (afterHide, afterShowAgain);
        });

        Assert.Equal(ExportPhase.Idle, afterHide);
        Assert.Equal(ExportPhase.Exporting, afterShowAgain);
    }

    [Fact]
    public void AChildWindowWithTheProgressTitleIsIgnored()
    {
        var (phase, scanned) = Watch(() =>
        {
            var window = CreateWindow("Owner");
            window.Show();
            Pump();
            var parameters = new HwndSourceParameters(Progress)
            {
                ParentWindow = new WindowInteropHelper(window).Handle,
                WindowStyle = ChildVisibleStyle,
                Width = 10,
                Height = 10,
            };
            using (var child = new HwndSource(parameters))
            {
                Pump();
                var phase = Detector.Phase;
                var scanned = ExportWindowWatcher.Scan();
                window.Close();
                Pump();
                return (phase, scanned);
            }
        });

        Assert.Equal(ExportPhase.Idle, phase);
        Assert.Equal(ExportPhase.Idle, scanned);
    }

    [Fact]
    public void WindowsShownOnAnotherThreadAreNotWatched()
    {
        var phase = Watch(() =>
        {
            var shown = RunSta(() =>
            {
                var window = CreateWindow(Progress);
                window.Show();
                Pump();
                window.Close();
                Pump();
                return true;
            });
            Pump();
            return shown ? Detector.Phase : ExportPhase.Exporting;
        });

        Assert.Equal(ExportPhase.Idle, phase);
    }

    [Fact]
    public void TheScanReportsAVisibleProgressWindow()
    {
        var (visible, hidden) = Watch(() =>
        {
            var window = CreateWindow(Progress);
            window.Show();
            var visible = ExportWindowWatcher.Scan();
            window.Hide();
            var hidden = ExportWindowWatcher.Scan();
            window.Close();
            Pump();
            return (visible, hidden);
        });

        Assert.Equal(ExportPhase.Exporting, visible);
        Assert.Equal(ExportPhase.Idle, hidden);
    }

    [Fact]
    public void TheScanReportsAVisibleConfigurationWindowAsPreparing()
    {
        var scanned = Watch(() =>
        {
            var window = CreateWindow(Configuration);
            window.Show();
            var scanned = ExportWindowWatcher.Scan();
            window.Close();
            Pump();
            return scanned;
        });

        Assert.Equal(ExportPhase.Preparing, scanned);
    }

    [Fact]
    public void TheScanPrefersTheProgressWindowOverTheConfigurationWindow()
    {
        var scanned = Watch(() =>
        {
            var configuration = CreateWindow(Configuration);
            var progress = CreateWindow(Progress);
            configuration.Show();
            progress.Show();
            var scanned = ExportWindowWatcher.Scan();
            progress.Close();
            configuration.Close();
            Pump();
            return scanned;
        });

        Assert.Equal(ExportPhase.Exporting, scanned);
    }

    [Fact]
    public void TheScanTreatsAModalDialogAsPreparing()
    {
        var (during, after) = Watch(() =>
        {
            var owner = CreateWindow("Owner");
            owner.Show();
            Pump();
            var dialog = CreateWindow("Dialog");
            dialog.Owner = owner;
            ExportPhase? during = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                during = ExportWindowWatcher.Scan();
                dialog.Close();
            }), DispatcherPriority.ContextIdle);
            dialog.ShowDialog();
            Pump();
            var after = ExportWindowWatcher.Scan();
            owner.Close();
            Pump();
            return (during, after);
        });

        Assert.Equal(ExportPhase.Preparing, during);
        Assert.Equal(ExportPhase.Idle, after);
    }

    [Fact]
    public void TheScanTreatsAModalDialogWithoutAnOwnerAsPreparing()
    {
        var (during, after) = Watch(() =>
        {
            var dialog = CreateWindow("Dialog");
            ExportPhase? during = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                during = ExportWindowWatcher.Scan();
                dialog.Close();
            }), DispatcherPriority.ContextIdle);
            dialog.ShowDialog();
            Pump();
            return (during, ExportWindowWatcher.Scan());
        });

        Assert.Equal(ExportPhase.Preparing, during);
        Assert.Equal(ExportPhase.Idle, after);
    }

    [Fact]
    public void TheScanTreatsADisabledVisibleWindowAsPreparing()
    {
        var (disabled, enabled) = Watch(() =>
        {
            var window = CreateWindow("Owner");
            window.Show();
            Pump();
            var handle = new WindowInteropHelper(window).Handle;
            EnableWindow(handle, false);
            var disabled = ExportWindowWatcher.Scan();
            EnableWindow(handle, true);
            var enabled = ExportWindowWatcher.Scan();
            window.Close();
            Pump();
            return (disabled, enabled);
        });

        Assert.Equal(ExportPhase.Preparing, disabled);
        Assert.Equal(ExportPhase.Idle, enabled);
    }

    [Fact]
    public void ThePhaseIsResolvedOnTheWatchingThread()
    {
        var (fromAnotherThread, fromTheWatchingThread) = Watch(() =>
        {
            var window = CreateWindow(Progress);
            window.Show();
            Pump();
            var resolved = Task.Run(ExportWindowWatcher.ResolvePhase);
            PumpUntil(() => resolved.IsCompleted);
            var fromTheWatchingThread = ExportWindowWatcher.ResolvePhase();
            window.Close();
            Pump();
            return (resolved.Result, fromTheWatchingThread);
        });

        Assert.Equal(ExportPhase.Exporting, fromAnotherThread);
        Assert.Equal(ExportPhase.Exporting, fromTheWatchingThread);
    }

    [Fact]
    public void WithoutAWatcherNothingCanBeResolved()
    {
        Assert.Null(ExportWindowWatcher.ResolvePhase());
        Assert.Null(ExportWindowWatcher.Scan());
    }

    [Fact]
    public void AttachingTwiceKeepsTheFirstWatcher()
    {
        var phase = Watch(() =>
        {
            ExportWindowWatcher.Attach(new ExportDetector(false, () => "x", () => "y", () => null), _ => { });
            var window = CreateWindow(Progress);
            window.Show();
            Pump();
            var phase = Detector.Phase;
            window.Close();
            Pump();
            return phase;
        });

        Assert.Equal(ExportPhase.Exporting, phase);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enable);
}
