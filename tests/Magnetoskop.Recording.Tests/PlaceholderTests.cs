using Magnetoskop.Core.Models;

namespace Magnetoskop.Recording.Tests;

public class RecordingProfileTests
{
    [Fact]
    public void Factories_CoverRequiredCodecs()
    {
        Assert.Equal(RecordingCodec.H264, RecordingProfile.CreateH264Access().VideoCodec);
        Assert.Equal(RecordingCodec.H265, RecordingProfile.CreateH265Access().VideoCodec);
        Assert.Equal(RecordingCodec.Ffv1, RecordingProfile.CreateFfv1Archival().VideoCodec);
        Assert.Equal(RecordingCodec.ProRes, RecordingProfile.CreateProResHq().VideoCodec);
    }

    [Fact]
    public void ArchivalProfile_PreservesInterlacingByDefault()
    {
        var ffv1 = RecordingProfile.CreateFfv1Archival();
        Assert.False(ffv1.AllowProcessing);
        Assert.Equal("mkv", ffv1.Container);
        Assert.Equal(1, ffv1.GopSize);
    }

    [Fact]
    public void Compatibility_RejectsInvalidPairs()
    {
        Assert.False(RecordingProfile.IsCompatible(RecordingCodec.Ffv1, "mp4"));
        Assert.False(RecordingProfile.IsCompatible(RecordingCodec.ProRes, "avi"));
        Assert.True(RecordingProfile.IsCompatible(RecordingCodec.H265, "mkv"));
    }

    [Fact]
    public void DisplayName_IncludesCodecContainerAndCrf()
    {
        var name = RecordingProfile.CreateH264Access().DisplayName;
        Assert.Contains("H.264", name);
        Assert.Contains("MP4", name);
        Assert.Contains("CRF", name);
    }
}
