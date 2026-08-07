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

    [Theory]
    [InlineData(RecordingAudioCodec.Aac, "mkv", true)]
    [InlineData(RecordingAudioCodec.Aac, "mp4", true)]
    [InlineData(RecordingAudioCodec.Aac, "mov", true)]
    [InlineData(RecordingAudioCodec.Aac, "avi", false)]
    [InlineData(RecordingAudioCodec.Flac, "mkv", true)]
    [InlineData(RecordingAudioCodec.Flac, "mp4", false)]
    [InlineData(RecordingAudioCodec.Flac, "avi", false)]
    [InlineData(RecordingAudioCodec.Flac, "mov", false)]
    [InlineData(RecordingAudioCodec.PcmS16Le, "avi", true)]
    [InlineData(RecordingAudioCodec.PcmS24Le, "mkv", true)]
    [InlineData(RecordingAudioCodec.PcmS16Le, "mov", true)]
    [InlineData(RecordingAudioCodec.PcmS16Le, "mp4", false)]
    [InlineData(RecordingAudioCodec.Mp3, "avi", true)]
    [InlineData(RecordingAudioCodec.Mp3, "mp4", true)]
    [InlineData(RecordingAudioCodec.Vorbis, "mkv", true)]
    [InlineData(RecordingAudioCodec.Vorbis, "mp4", false)]
    public void AudioCompatibility_MatchesMuxMatrix(RecordingAudioCodec audio, string container, bool expected)
    {
        Assert.Equal(expected, RecordingProfile.IsAudioCompatible(audio, container));
    }

    [Fact]
    public void CoerceAudioCodec_KeepsCompatibleChoice()
    {
        var kept = RecordingProfile.CoerceAudioCodec(
            RecordingAudioCodec.Flac, RecordingCodec.Ffv1, "mkv");
        Assert.Equal(RecordingAudioCodec.Flac, kept);
    }

    [Fact]
    public void CoerceAudioCodec_ReplacesIllegalChoice()
    {
        var coerced = RecordingProfile.CoerceAudioCodec(
            RecordingAudioCodec.Flac, RecordingCodec.H264, "mp4");
        Assert.Equal(RecordingAudioCodec.Aac, coerced);
        Assert.True(RecordingProfile.IsAudioCompatible(coerced, "mp4"));
    }

    [Fact]
    public void FormatAudioCodec_UsesFriendlyNames()
    {
        Assert.Equal("AAC", RecordingProfile.FormatAudioCodec(RecordingAudioCodec.Aac));
        Assert.Equal("FLAC", RecordingProfile.FormatAudioCodec(RecordingAudioCodec.Flac));
        Assert.Equal("PCM 24", RecordingProfile.FormatAudioCodec(RecordingAudioCodec.PcmS24Le));
        Assert.Equal("MP3", RecordingProfile.FormatAudioCodec(RecordingAudioCodec.Mp3));
        Assert.Equal("Vorbis", RecordingProfile.FormatAudioCodec(RecordingAudioCodec.Vorbis));
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
