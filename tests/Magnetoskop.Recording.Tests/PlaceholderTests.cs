using Magnetoskop.Core.Models;

namespace Magnetoskop.Recording.Tests;

/// <summary>FFmpeg recorder tests arrive in Phase 6/7; profile defaults are validated here.</summary>
public class RecordingProfileTests
{
    [Fact]
    public void Defaults_ContainAllRequiredProfiles()
    {
        var codecs = RecordingProfile.Defaults.Select(p => p.VideoCodec).ToHashSet();
        Assert.Contains(RecordingCodec.H264, codecs);
        Assert.Contains(RecordingCodec.Ffv1, codecs);
        Assert.Contains(RecordingCodec.ProRes, codecs);
    }

    [Fact]
    public void ArchivalProfile_PreservesInterlacingByDefault()
    {
        var ffv1 = RecordingProfile.Defaults.Single(p => p.VideoCodec == RecordingCodec.Ffv1);
        Assert.False(ffv1.AllowProcessing);
        Assert.Equal("mkv", ffv1.Container);
    }
}