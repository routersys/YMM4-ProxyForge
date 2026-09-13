using ProxyForge.Encoding;

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
    public void VendorOfMapsTheEncoderSuffixToAVendorId(string encoder, int expected)
        => Assert.Equal(expected, FFmpegEncoderCandidates.VendorOf(encoder));

    [Fact]
    public void TheCandidatesAreNvencQsvAmfInThatOrder()
    {
        string[] expected = ["h264_nvenc", "h264_qsv", "h264_amf"];

        Assert.Equal(expected, Candidates());
    }

    [Fact]
    public void WithoutADiscreteGpuTheDeclaredOrderIsKept()
    {
        string[] expected = ["h264_nvenc", "h264_qsv", "h264_amf"];

        Assert.Equal(expected, Order([]));
    }

    [Fact]
    public void ADiscreteAmdGpuPromotesAmfAboveIntegratedIntel()
    {
        string[] expected = ["h264_amf", "h264_nvenc", "h264_qsv"];

        Assert.Equal(expected, Order([FFmpegEncoderCandidates.VendorAmd]));
    }

    [Fact]
    public void ADiscreteNvidiaGpuKeepsNvencFirst()
    {
        string[] expected = ["h264_nvenc", "h264_qsv", "h264_amf"];

        Assert.Equal(expected, Order([FFmpegEncoderCandidates.VendorNvidia]));
    }

    [Fact]
    public void ADiscreteIntelGpuPromotesQsv()
    {
        string[] expected = ["h264_qsv", "h264_nvenc", "h264_amf"];

        Assert.Equal(expected, Order([FFmpegEncoderCandidates.VendorIntel]));
    }

    [Fact]
    public void SeveralDiscreteVendorsKeepTheirRelativeOrder()
    {
        string[] expected = ["h264_nvenc", "h264_amf", "h264_qsv"];

        Assert.Equal(expected, Order([FFmpegEncoderCandidates.VendorAmd, FFmpegEncoderCandidates.VendorNvidia]));
    }

    [Fact]
    public void TheOrderIsAPermutationOfTheInput()
    {
        var ordered = Order([FFmpegEncoderCandidates.VendorAmd]);

        Assert.Equal(Candidates().Order(), ordered.Order());
    }

    [Fact]
    public void ATooSmallDestinationIsRejected()
    {
        var destination = new string[FFmpegEncoderCandidates.Hardware264.Length - 1];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FFmpegEncoderCandidates.Order(FFmpegEncoderCandidates.Hardware264, new HashSet<int>(), destination));
    }

    static string[] Candidates() => FFmpegEncoderCandidates.Hardware264.ToArray();

    static string[] Order(int[] discreteVendors)
    {
        var destination = new string[FFmpegEncoderCandidates.Hardware264.Length];
        FFmpegEncoderCandidates.Order(FFmpegEncoderCandidates.Hardware264, new HashSet<int>(discreteVendors), destination);
        return destination;
    }
}
