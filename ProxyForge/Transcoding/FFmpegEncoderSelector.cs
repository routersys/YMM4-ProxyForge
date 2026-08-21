using System.Collections.Concurrent;
using System.Diagnostics;
using Vortice.DXGI;

namespace ProxyForge.Transcoding;

internal static class FFmpegEncoderCandidates
{
    internal const int VendorNvidia = 0x10DE;
    internal const int VendorAmd = 0x1002;
    internal const int VendorIntel = 0x8086;

    private static readonly string[] Hardware = ["h264_nvenc", "h264_qsv", "h264_amf"];

    internal static ReadOnlySpan<string> Hardware264 => Hardware;

    internal static int VendorOf(string encoder)
    {
        if (encoder.Contains("nvenc", StringComparison.Ordinal))
            return VendorNvidia;
        if (encoder.Contains("amf", StringComparison.Ordinal))
            return VendorAmd;
        if (encoder.Contains("qsv", StringComparison.Ordinal))
            return VendorIntel;
        return 0;
    }

    internal static void Order(
        ReadOnlySpan<string> candidates,
        IReadOnlySet<int> discreteVendors,
        Span<string> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, candidates.Length);

        var index = 0;
        for (var i = 0; i < candidates.Length; i++)
        {
            if (discreteVendors.Contains(VendorOf(candidates[i])))
                destination[index++] = candidates[i];
        }

        for (var i = 0; i < candidates.Length; i++)
        {
            if (!discreteVendors.Contains(VendorOf(candidates[i])))
                destination[index++] = candidates[i];
        }
    }
}

internal static class FFmpegEncoderSelector
{
    private const int ProbeTimeoutMilliseconds = 15_000;
    private const long DiscreteVideoMemoryThreshold = 512L * 1024L * 1024L;

    private static readonly ConcurrentDictionary<long, string> ResolvedEncoders = new();

    private static readonly Lazy<IReadOnlySet<int>> DiscreteGpuVendors =
        new(DetectDiscreteGpuVendors, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static async Task<string> ResolveAsync(
        string ffmpegPath,
        bool hardwareAccelerationEnabled,
        uint width,
        uint height,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!hardwareAccelerationEnabled)
            return FFmpegArguments.SoftwareVideoEncoder;

        var key = ((long)width << 32) | height;
        if (ResolvedEncoders.TryGetValue(key, out var cached))
            return cached;

        var candidates = FFmpegEncoderCandidates.Hardware264;
        var ordered = new string[candidates.Length];
        FFmpegEncoderCandidates.Order(candidates, DiscreteGpuVendors.Value, ordered);

        foreach (var candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await ProbeEncoderAsync(ffmpegPath, candidate, width, height, workingDirectory, cancellationToken)
                    .ConfigureAwait(false))
                continue;

            ResolvedEncoders[key] = candidate;
            return candidate;
        }

        ResolvedEncoders[key] = FFmpegArguments.SoftwareVideoEncoder;
        return FFmpegArguments.SoftwareVideoEncoder;
    }

    private static async Task<bool> ProbeEncoderAsync(
        string ffmpegPath,
        string encoder,
        uint width,
        uint height,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeoutMilliseconds);

        try
        {
            var result = await FFmpegProcessRunner.RunAsync(
                ffmpegPath,
                arguments => FFmpegArguments.WriteEncoderProbe(arguments, encoder, width, height),
                workingDirectory,
                null,
                null,
                ProcessPriorityClass.BelowNormal,
                timeout.Token).ConfigureAwait(false);

            return result.IsSuccess;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine(string.Concat("[FFmpegEncoderSelector] Probe of ", encoder, " failed: ", ex.Message));
            return false;
        }
    }

    private static IReadOnlySet<int> DetectDiscreteGpuVendors()
    {
        var vendors = new HashSet<int>();

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            var index = 0;
            while (factory.EnumAdapters1(index, out var adapter).Success)
            {
                using (adapter)
                {
                    var description = adapter.Description1;
                    if ((description.Flags & AdapterFlags.Software) == 0 &&
                        (long)description.DedicatedVideoMemory >= DiscreteVideoMemoryThreshold)
                        vendors.Add(description.VendorId);
                }

                index++;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[FFmpegEncoderSelector] Adapter enumeration failed: ", ex.Message));
        }

        return vendors;
    }
}
