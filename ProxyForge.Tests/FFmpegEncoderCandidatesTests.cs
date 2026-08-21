using ProxyForge.Transcoding;
using Xunit;

namespace ProxyForge.Tests;

public sealed class FFmpegEncoderCandidatesTests
{
    [Theory]
    [InlineData("h264_nvenc", FFmpegEncoderCandidates.VendorNvidia)]
    [InlineData("av1_nvenc", FFmpegEncoderCandidates.VendorNvidia)]
    [InlineData("h264_amf", FFmpegEncoderCandidates.VendorAmd)]
    [InlineData("h264_qsv", FFmpegEncoderCandidates.VendorIntel)]
    [InlineData("h264", 0)]
    [InlineData("libopenh264", 0)]
    public void VendorOf_MapsEncoderSuffixToVendorId(string encoder, int expected) =>
        Assert.Equal(expected, FFmpegEncoderCandidates.VendorOf(encoder));

    [Fact]
    public void Hardware264_ListsNvencQsvAmfInThatOrder()
    {
        string[] expected = ["h264_nvenc", "h264_qsv", "h264_amf"];

        Assert.Equal(expected, Candidates());
    }

    [Fact]
    public void Order_WithoutDiscreteGpu_PreservesDeclaredOrder()
    {
        string[] expected = ["h264_nvenc", "h264_qsv", "h264_amf"];

        Assert.Equal(expected, Order([]));
    }

    [Fact]
    public void Order_WithDiscreteAmd_PromotesAmfAboveIntegratedIntel()
    {
        string[] expected = ["h264_amf", "h264_nvenc", "h264_qsv"];

        Assert.Equal(expected, Order([FFmpegEncoderCandidates.VendorAmd]));
    }

    [Fact]
    public void Order_WithDiscreteNvidia_KeepsNvencFirst()
    {
        string[] expected = ["h264_nvenc", "h264_qsv", "h264_amf"];

        Assert.Equal(expected, Order([FFmpegEncoderCandidates.VendorNvidia]));
    }

    [Fact]
    public void Order_WithDiscreteIntel_PromotesQsv()
    {
        string[] expected = ["h264_qsv", "h264_nvenc", "h264_amf"];

        Assert.Equal(expected, Order([FFmpegEncoderCandidates.VendorIntel]));
    }

    [Fact]
    public void Order_WithSeveralDiscreteVendors_KeepsRelativeOrderStable()
    {
        string[] expected = ["h264_nvenc", "h264_amf", "h264_qsv"];

        Assert.Equal(expected, Order([FFmpegEncoderCandidates.VendorAmd, FFmpegEncoderCandidates.VendorNvidia]));
    }

    [Fact]
    public void Order_IsAPermutationOfTheInput()
    {
        var ordered = Order([FFmpegEncoderCandidates.VendorAmd]);

        Assert.Equal(Candidates().Order(), ordered.Order());
    }

    [Fact]
    public void Order_DestinationTooSmall_Throws()
    {
        var destination = new string[FFmpegEncoderCandidates.Hardware264.Length - 1];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FFmpegEncoderCandidates.Order(FFmpegEncoderCandidates.Hardware264, new HashSet<int>(), destination));
    }

    private static string[] Candidates() => FFmpegEncoderCandidates.Hardware264.ToArray();

    private static string[] Order(int[] discreteVendors)
    {
        var destination = new string[FFmpegEncoderCandidates.Hardware264.Length];
        FFmpegEncoderCandidates.Order(FFmpegEncoderCandidates.Hardware264, new HashSet<int>(discreteVendors), destination);
        return destination;
    }
}
