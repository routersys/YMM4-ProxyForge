using System.Windows;
using System.Windows.Media;
using ProxyForge.Encoding;

namespace ProxyForge.Views;

internal sealed class ChunkCoverageBar : FrameworkElement
{
    public static readonly DependencyProperty CoverageProperty = DependencyProperty.Register(
        nameof(Coverage), typeof(ProxyChunkCoverage), typeof(ChunkCoverageBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ChunkCoverageBar),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public ProxyChunkCoverage? Coverage
    {
        get => (ProxyChunkCoverage?)GetValue(CoverageProperty);
        set => SetValue(CoverageProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public static IEnumerable<(double Start, double Length)> Segments(ProxyChunkCoverage coverage)
    {
        if (coverage.ChunkCount <= 0)
            yield break;

        double count = coverage.ChunkCount;
        var chunks = coverage.Chunks.Where(chunk => chunk >= 0 && chunk < coverage.ChunkCount).Distinct().Order().ToArray();
        var index = 0;
        while (index < chunks.Length)
        {
            var first = chunks[index];
            var last = first;
            while (index + 1 < chunks.Length && chunks[index + 1] == last + 1)
                last = chunks[++index];

            yield return (first / count, (last - first + 1) / count);
            index++;
        }

        if (coverage.CurrentChunk is { } current && current >= 0 && current < coverage.ChunkCount && !coverage.Contains(current) && coverage.CurrentProgress > 0d)
            yield return (current / count, Math.Min(coverage.CurrentProgress, 1d) / count);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Coverage is not { } coverage)
            return;

        var width = RenderSize.Width;
        var height = RenderSize.Height;
        foreach (var (start, length) in Segments(coverage))
            drawingContext.DrawRectangle(Fill, null, new Rect(start * width, 0d, length * width, height));
    }
}
