using System.Collections.Concurrent;
using System.Diagnostics;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;

namespace ProxyForge.Encoding;

internal static class FFmpegEncoderCandidates
{
    public const int VendorNvidia = 0x10DE;
    public const int VendorAmd = 0x1002;
    public const int VendorIntel = 0x8086;

    static readonly string[] Hardware = ["h264_nvenc", "h264_qsv", "h264_amf"];

    public static ReadOnlySpan<string> Hardware264 => Hardware;

    public static int VendorOf(string encoder)
    {
        if (encoder.Contains("nvenc", StringComparison.Ordinal))
            return VendorNvidia;
        if (encoder.Contains("amf", StringComparison.Ordinal))
            return VendorAmd;
        if (encoder.Contains("qsv", StringComparison.Ordinal))
            return VendorIntel;
        return 0;
    }

    public static void Order(ReadOnlySpan<string> candidates, IReadOnlySet<int> discreteVendors, Span<string> destination)
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
    const int ProbeTimeoutMilliseconds = 15_000;
    const long DiscreteVideoMemoryThreshold = 512L * 1024L * 1024L;

    static readonly ConcurrentDictionary<long, string> ResolvedEncoders = new();

    static readonly Lazy<IReadOnlySet<int>> DiscreteGpuVendors =
        new(DetectDiscreteGpuVendors, LazyThreadSafetyMode.ExecutionAndPublication);

    public static async Task<string> ResolveAsync(
        string ffmpegPath,
        bool hardwareEncoderEnabled,
        int width,
        int height,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!hardwareEncoderEnabled)
            return FFmpegArguments.SoftwareVideoEncoder;

        var key = ((long)width << 32) | (uint)height;
        if (ResolvedEncoders.TryGetValue(key, out var cached))
            return cached;

        var candidates = FFmpegEncoderCandidates.Hardware264;
        var ordered = new string[candidates.Length];
        FFmpegEncoderCandidates.Order(candidates, DiscreteGpuVendors.Value, ordered);

        foreach (var candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await ProbeEncoderAsync(ffmpegPath, candidate, width, height, workingDirectory, cancellationToken).ConfigureAwait(false))
                continue;

            Log.Default.Write($"ProxyForge: ハードウェアエンコーダー '{candidate}' を使用します。({width}x{height})");
            ResolvedEncoders[key] = candidate;
            return candidate;
        }

        Log.Default.Write($"ProxyForge: 使えるハードウェアエンコーダーが見つからず、ソフトウェアエンコードを使用します。({width}x{height})");
        ResolvedEncoders[key] = FFmpegArguments.SoftwareVideoEncoder;
        return FFmpegArguments.SoftwareVideoEncoder;
    }

    static async Task<bool> ProbeEncoderAsync(
        string ffmpegPath,
        string encoder,
        int width,
        int height,
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
                ProcessPriorityClass.BelowNormal,
                timeout.Token).ConfigureAwait(false);

            return result.IsSuccess;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (InvalidOperationException exception)
        {
            Log.Default.Write($"ProxyForge: エンコーダー '{encoder}' の確認に失敗しました。", exception);
            return false;
        }
    }

    static IReadOnlySet<int> DetectDiscreteGpuVendors()
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
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Default.Write("ProxyForge: アダプターの列挙に失敗しました。", exception);
        }

        return vendors;
    }
}
