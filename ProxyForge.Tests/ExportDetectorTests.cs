using ProxyForge.Export;

namespace ProxyForge.Tests;

public sealed class ExportDetectorTests
{
    const string Configuration = "動画出力";
    const string Progress = "出力";

    static ExportDetector Create(bool commandLineEncode, Func<Func<bool>, bool?>? resolve = null, List<int>? resolveCalls = null)
        => new(
            commandLineEncode,
            () => Configuration,
            () => Progress,
            probe =>
            {
                resolveCalls?.Add(1);
                return resolve is null ? probe() : resolve(probe);
            });

    [Theory]
    [InlineData(new[] { "--encode", @"C:\project.ymmp" }, true)]
    [InlineData(new[] { "YukkuriMovieMaker.exe", "--ENCODE", @"C:\project.ymmp", "--output", @"C:\out.mp4" }, true)]
    [InlineData(new[] { "--encode" }, false)]
    [InlineData(new[] { "--encode", "--output" }, false)]
    [InlineData(new[] { "--encode", " " }, false)]
    [InlineData(new[] { @"C:\project.ymmp" }, false)]
    [InlineData(new string[0], false)]
    public void TheEncodeOptionIsRecognisedLikeTheHost(string[] arguments, bool expected)
        => Assert.Equal(expected, ExportDetector.HasEncodeOption(arguments));

    [Theory]
    [InlineData(Configuration, ExportWindowRole.Configuration)]
    [InlineData(Progress, ExportWindowRole.Progress)]
    [InlineData("", ExportWindowRole.None)]
    [InlineData(null, ExportWindowRole.None)]
    [InlineData("出力 ", ExportWindowRole.None)]
    [InlineData("YukkuriMovieMaker4", ExportWindowRole.None)]
    public void WindowsAreClassifiedByTheirExactTitle(string? title, ExportWindowRole expected)
        => Assert.Equal(expected, Create(false).Classify(title));

    [Fact]
    public void TheCommandLineEncodeAlwaysExports()
    {
        var calls = new List<int>();
        var detector = Create(true, resolveCalls: calls);

        Assert.True(detector.IsCommandLineEncode);
        Assert.True(detector.IsExporting());
        Assert.Empty(calls);
        Assert.Equal(ExportPhase.Idle, detector.Phase);
    }

    [Fact]
    public void NothingIsExportingAtFirst()
    {
        var calls = new List<int>();
        var detector = Create(false, resolveCalls: calls);

        Assert.False(detector.IsExporting());
        Assert.Empty(calls);
    }

    [Fact]
    public void TheProgressWindowMeansExportingUntilItCloses()
    {
        var calls = new List<int>();
        var detector = Create(false, resolveCalls: calls);

        detector.OnWindowOpened(ExportWindowRole.Progress);

        Assert.Equal(ExportPhase.Exporting, detector.Phase);
        Assert.True(detector.IsExporting());
        Assert.Empty(calls);

        detector.OnWindowClosed(ExportWindowRole.Progress);

        Assert.Equal(ExportPhase.Idle, detector.Phase);
        Assert.False(detector.IsExporting());
    }

    [Fact]
    public void TheConfigurationWindowStartsThePreparation()
    {
        var detector = Create(false);

        detector.OnWindowOpened(ExportWindowRole.Configuration);

        Assert.Equal(ExportPhase.Preparing, detector.Phase);
    }

    [Fact]
    public void ClosingTheConfigurationWindowKeepsThePreparation()
    {
        var detector = Create(false);
        detector.OnWindowOpened(ExportWindowRole.Configuration);

        detector.OnWindowClosed(ExportWindowRole.Configuration);

        Assert.Equal(ExportPhase.Preparing, detector.Phase);
    }

    [Fact]
    public void AnUnrelatedWindowChangesNothing()
    {
        var detector = Create(false);

        detector.OnWindowOpened(ExportWindowRole.None);
        detector.OnWindowClosed(ExportWindowRole.None);

        Assert.Equal(ExportPhase.Idle, detector.Phase);
    }

    [Fact]
    public void DuringThePreparationTheUiThreadDecidesThatExportingHasStarted()
    {
        ExportDetector? detector = null;
        detector = Create(false, probe =>
        {
            detector!.OnWindowOpened(ExportWindowRole.Progress);
            return probe();
        });
        detector.OnWindowOpened(ExportWindowRole.Configuration);

        Assert.True(detector.IsExporting());
        Assert.Equal(ExportPhase.Exporting, detector.Phase);
    }

    [Fact]
    public void DuringThePreparationTheUiThreadDecidesThatItWasCancelled()
    {
        var calls = new List<int>();
        var detector = Create(false, resolveCalls: calls);
        detector.OnWindowOpened(ExportWindowRole.Configuration);

        Assert.False(detector.IsExporting());
        Assert.Equal(ExportPhase.Idle, detector.Phase);
        Assert.Single(calls);
        Assert.False(detector.IsExporting());
        Assert.Single(calls);
    }

    [Fact]
    public void AnUnresolvedPreparationIsTreatedAsExporting()
    {
        var detector = Create(false, _ => null);
        detector.OnWindowOpened(ExportWindowRole.Configuration);

        Assert.True(detector.IsExporting());
        Assert.Equal(ExportPhase.Preparing, detector.Phase);
    }

    [Fact]
    public void AConfigurationWindowDuringAnExportDoesNotDemoteThePhase()
    {
        var detector = Create(false);
        detector.OnWindowOpened(ExportWindowRole.Progress);

        detector.OnWindowOpened(ExportWindowRole.Configuration);

        Assert.Equal(ExportPhase.Exporting, detector.Phase);
    }

    [Fact]
    public void TheProgressWindowEndsAPreparationThatWasResolvedTooEarly()
    {
        var detector = Create(false);
        detector.OnWindowOpened(ExportWindowRole.Configuration);
        Assert.False(detector.IsExporting());

        detector.OnWindowOpened(ExportWindowRole.Progress);

        Assert.True(detector.IsExporting());
    }
}
