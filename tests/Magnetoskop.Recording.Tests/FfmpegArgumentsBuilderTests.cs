using Magnetoskop.Core.Models;
using Magnetoskop.Recording;

namespace Magnetoskop.Recording.Tests;

public class FfmpegArgumentsBuilderTests
{
    private static readonly VideoFormat Pal = new()
    {
        Width = 720,
        Height = 576,
        FrameRate = 25.0,
        PixelFormat = VideoPixelFormat.Bgr24,
        Interlaced = true,
        TopFieldFirst = true,
    };

    private static readonly AudioFormat Pcm48k = new()
    {
        SampleRate = 48000,
        Channels = 2,
        BitsPerSample = 16,
    };

    private static RecordingProfile Profile(string id)
        => RecordingProfile.Defaults.Single(p => p.Id == id);

    private static IReadOnlyList<string> Build(RecordingProfile profile, VideoFormat? video = null,
        AudioFormat? audio = null, string? pipe = null, string output = "out.file")
        => FfmpegArgumentsBuilder.Build(profile, video ?? Pal, audio, pipe, output);

    private static bool ContainsPair(IReadOnlyList<string> args, string key, string value)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == key && args[i + 1] == value) return true;
        }
        return false;
    }

    // ---- FFV1 archival (Phase 6) --------------------------------------------

    [Fact]
    public void Ffv1_UsesArchivalSettings()
    {
        var args = Build(Profile("ffv1-archival"), audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mkv");

        Assert.True(ContainsPair(args, "-c:v", "ffv1"));
        Assert.True(ContainsPair(args, "-level", "3"));
        Assert.True(ContainsPair(args, "-g", "1"));
        Assert.True(ContainsPair(args, "-slicecrc", "1"));
        Assert.True(ContainsPair(args, "-c:a", "pcm_s24le"));
        Assert.Equal("out.mkv", args[^1]);
    }

    [Fact]
    public void Ffv1_PreservesInterlacingWithFieldOrderTag()
    {
        var args = Build(Profile("ffv1-archival"), audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-field_order", "tt"));
        // No deinterlacing filter must be present.
        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain("yadif", string.Join(" ", args));
    }

    [Fact]
    public void BottomFieldFirst_TagsBb()
    {
        var bff = Pal with { TopFieldFirst = false };
        var args = Build(Profile("ffv1-archival"), video: bff, audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-field_order", "bb"));
    }

    [Fact]
    public void ProgressiveSource_HasNoFieldOrder()
    {
        var progressive = Pal with { Interlaced = false };
        var args = Build(Profile("ffv1-archival"), video: progressive, audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.DoesNotContain("-field_order", args);
    }

    // ---- H.264 / ProRes (Phase 7) --------------------------------------------

    [Fact]
    public void H264_UsesCrfPresetAndFaststart()
    {
        var profile = Profile("h264-access");
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mp4");

        Assert.True(ContainsPair(args, "-c:v", "libx264"));
        Assert.True(ContainsPair(args, "-crf", profile.Crf.ToString()));
        Assert.True(ContainsPair(args, "-preset", profile.Preset));
        Assert.True(ContainsPair(args, "-c:a", "aac"));
        Assert.True(ContainsPair(args, "-movflags", "+faststart"));
    }

    [Fact]
    public void H264_InterlacedSource_EnablesInterlacedEncodingFlags()
    {
        var args = Build(Profile("h264-access"), audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-flags", "+ildct+ilme"));
    }

    [Fact]
    public void ProRes_UsesProresKsWithProfileAnd10Bit()
    {
        var profile = Profile("prores-hq");
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mov");

        Assert.True(ContainsPair(args, "-c:v", "prores_ks"));
        Assert.True(ContainsPair(args, "-profile:v", profile.ProResProfile.ToString()));
        Assert.True(ContainsPair(args, "-pix_fmt", "yuv422p10le"));
        Assert.True(ContainsPair(args, "-c:a", "pcm_s16le"));
    }

    // ---- Inputs -----------------------------------------------------------------

    [Fact]
    public void VideoInput_DescribesRawFramesOnStdin()
    {
        var args = Build(Profile("ffv1-archival"), audio: Pcm48k, pipe: @"\\.\pipe\a");

        Assert.True(ContainsPair(args, "-f", "rawvideo"));
        Assert.True(ContainsPair(args, "-pix_fmt", "bgr24"));
        Assert.True(ContainsPair(args, "-video_size", "720x576"));
        Assert.True(ContainsPair(args, "-framerate", "25"));
        Assert.True(ContainsPair(args, "-i", "pipe:0"));
    }

    [Fact]
    public void AudioInput_DescribesPcmPipe()
    {
        var args = Build(Profile("ffv1-archival"), audio: Pcm48k, pipe: @"\\.\pipe\a");

        Assert.True(ContainsPair(args, "-f", "s16le"));
        Assert.True(ContainsPair(args, "-ar", "48000"));
        Assert.True(ContainsPair(args, "-ac", "2"));
        Assert.True(ContainsPair(args, "-i", @"\\.\pipe\a"));
    }

    [Fact]
    public void VideoOnly_OmitsAudioArguments()
    {
        var args = Build(Profile("ffv1-archival"));
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("-ar", args);
    }

    [Fact]
    public void AudioWithoutPipePath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            FfmpegArgumentsBuilder.Build(Profile("ffv1-archival"), Pal, Pcm48k, null, "out.mkv"));
    }

    // ---- Helpers -----------------------------------------------------------------

    [Theory]
    [InlineData(25.0, "25")]
    [InlineData(50.0, "50")]
    [InlineData(29.97, "30000/1001")]
    [InlineData(23.976, "24000/1001")]
    [InlineData(12.5, "12.5")]
    public void FrameRate_FormatsExactRationals(double fps, string expected)
    {
        Assert.Equal(expected, FfmpegArgumentsBuilder.FormatFrameRate(fps));
    }

    [Theory]
    [InlineData(16, "s16le")]
    [InlineData(24, "s24le")]
    [InlineData(32, "s32le")]
    public void PcmInputFormat_MapsBitDepths(int bits, string expected)
    {
        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = bits };
        Assert.Equal(expected, FfmpegArgumentsBuilder.PcmInputFormat(format));
    }

    [Fact]
    public void PcmInputFormat_RejectsUnknownBitDepth()
    {
        var format = new AudioFormat { SampleRate = 48000, Channels = 2, BitsPerSample = 8 };
        Assert.Throws<NotSupportedException>(() => FfmpegArgumentsBuilder.PcmInputFormat(format));
    }

    [Fact]
    public void Join_QuotesArgumentsWithSpaces()
    {
        var joined = FfmpegArgumentsBuilder.Join(new[] { "-i", @"C:\My Videos\out.mkv" });
        Assert.Equal(@"-i ""C:\My Videos\out.mkv""", joined);
    }
}