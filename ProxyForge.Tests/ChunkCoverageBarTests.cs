using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProxyForge.Encoding;
using ProxyForge.Views;

namespace ProxyForge.Tests;

[Collection("Wpf")]
public sealed class ChunkCoverageBarTests
{
    static (double Start, double Length)[] SegmentsOf(ProxyChunkCoverage coverage) => [.. ChunkCoverageBar.Segments(coverage)];

    [Fact]
    public void NothingIsDrawnWithoutChunks()
    {
        Assert.Empty(SegmentsOf(ProxyChunkCoverage.Empty));
        Assert.Empty(SegmentsOf(new ProxyChunkCoverage(4, [], null, 0d)));
    }

    [Fact]
    public void CoveredChunksAreDrawnAtTheirPositions()
    {
        var segments = SegmentsOf(new ProxyChunkCoverage(4, [3, 1], null, 0d));

        Assert.Equal([(0.25d, 0.25d), (0.75d, 0.25d)], segments);
    }

    [Fact]
    public void AdjacentChunksAreMergedIntoOneSegment()
    {
        var segments = SegmentsOf(new ProxyChunkCoverage(5, [0, 1, 2, 4], null, 0d));

        Assert.Equal([(0d, 0.6d), (0.8d, 0.2d)], segments);
    }

    [Fact]
    public void ChunksOutsideTheLayoutAndDuplicatesAreIgnored()
    {
        var segments = SegmentsOf(new ProxyChunkCoverage(4, [7, -1, 2, 2], null, 0d));

        Assert.Equal([(0.5d, 0.25d)], segments);
    }

    [Fact]
    public void TheChunkBeingGeneratedIsDrawnPartially()
    {
        var segments = SegmentsOf(new ProxyChunkCoverage(4, [0], 2, 0.5d));

        Assert.Equal([(0d, 0.25d), (0.5d, 0.125d)], segments);
    }

    [Fact]
    public void TheCurrentChunkIsNotDrawnTwiceOrWhenNothingIsDone()
    {
        Assert.Equal([(0.5d, 0.25d)], SegmentsOf(new ProxyChunkCoverage(4, [2], 2, 0.5d)));
        Assert.Empty(SegmentsOf(new ProxyChunkCoverage(4, [], 2, 0d)));
        Assert.Empty(SegmentsOf(new ProxyChunkCoverage(4, [], 9, 0.5d)));
        Assert.Equal([(0.5d, 0.25d)], SegmentsOf(new ProxyChunkCoverage(4, [], 2, 1.5d)));
    }

    [Fact]
    public void RendersGreenWhereChunksExist()
    {
        var pixels = RunSta(() =>
        {
            var bar = new ChunkCoverageBar
            {
                Coverage = new ProxyChunkCoverage(4, [1], 2, 0.5d),
                Fill = Brushes.Lime,
            };
            bar.Measure(new Size(100d, 10d));
            bar.Arrange(new Rect(0d, 0d, 100d, 10d));
            bar.UpdateLayout();
            var bitmap = new RenderTargetBitmap(100, 10, 96d, 96d, PixelFormats.Pbgra32);
            bitmap.Render(bar);
            var buffer = new byte[100 * 10 * 4];
            bitmap.CopyPixels(buffer, 100 * 4, 0);
            return buffer;
        });

        Assert.Equal(0, AlphaAt(pixels, 10));
        Assert.Equal(255, AlphaAt(pixels, 30));
        Assert.Equal(255, GreenAt(pixels, 30));
        Assert.Equal(0, RedAt(pixels, 30));
        Assert.Equal(255, AlphaAt(pixels, 55));
        Assert.Equal(0, AlphaAt(pixels, 70));
        Assert.Equal(0, AlphaAt(pixels, 90));
    }

    static byte AlphaAt(byte[] pixels, int x) => pixels[5 * 100 * 4 + x * 4 + 3];

    static byte GreenAt(byte[] pixels, int x) => pixels[5 * 100 * 4 + x * 4 + 1];

    static byte RedAt(byte[] pixels, int x) => pixels[5 * 100 * 4 + x * 4 + 2];

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
}
