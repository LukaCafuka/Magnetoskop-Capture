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

    // ---- FFV1 archival -------------------------------------------------------

    [Fact]
    public void Ffv1_UsesArchivalSettings()
    {
        var profile = RecordingProfile.CreateFfv1Archival();
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mkv");

        Assert.True(ContainsPair(args, "-c:v", "ffv1"));
        Assert.True(ContainsPair(args, "-level", "3"));
        Assert.True(ContainsPair(args, "-g", "1"));
        Assert.True(ContainsPair(args, "-slicecrc", "1"));
        Assert.True(ContainsPair(args, "-c:a", "pcm_s24le"));
        Assert.Equal("out.mkv", args[^1]);
    }

    [Fact]
    public void Ffv1_PreservesInterlacingWithSetfieldAndFieldOrder()
    {
        var args = Build(RecordingProfile.CreateFfv1Archival(), audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-vf", "setfield=tff"));
        Assert.True(ContainsPair(args, "-field_order", "tt"));
        Assert.DoesNotContain("yadif", string.Join(" ", args));
        Assert.DoesNotContain("separatefields", string.Join(" ", args));
    }

    [Fact]
    public void BottomFieldFirst_TagsBffAndBb()
    {
        var bff = Pal with { TopFieldFirst = false };
        var args = Build(RecordingProfile.CreateFfv1Archival(), video: bff, audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-vf", "setfield=bff"));
        Assert.True(ContainsPair(args, "-field_order", "bb"));
    }

    [Fact]
    public void ProgressiveSource_TagsProgAndProgressive()
    {
        var progressive = Pal with { Interlaced = false };
        var args = Build(RecordingProfile.CreateFfv1Archival(), video: progressive, audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-vf", "setfield=prog"));
        Assert.True(ContainsPair(args, "-field_order", "progressive"));
    }

    [Fact]
    public void H265_InterlacedSource_UsesSetfieldAndFieldOrder()
    {
        var args = Build(RecordingProfile.CreateH265Access(), audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-vf", "setfield=tff"));
        Assert.True(ContainsPair(args, "-field_order", "tt"));
    }

    [Fact]
    public void ProRes_InterlacedSource_UsesSetfieldAndFieldOrder()
    {
        var args = Build(RecordingProfile.CreateProResHq(), audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mov");
        Assert.True(ContainsPair(args, "-vf", "setfield=tff"));
        Assert.True(ContainsPair(args, "-field_order", "tt"));
    }

    [Fact]
    public void InterlacedAllowProcessing_UsesYadif2xAndTagsProgressive()
    {
        var profile = RecordingProfile.CreateH264Access() with { AllowProcessing = true };
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a");

        Assert.True(ContainsPair(args, "-vf", "setfield=tff,yadif=1:0:0"));
        Assert.True(ContainsPair(args, "-field_order", "progressive"));
        Assert.DoesNotContain("+ildct+ilme", string.Join(" ", args));
        Assert.True(FfmpegArgumentsBuilder.WillDeinterlace(Pal, profile));
        Assert.Equal(50.0, FfmpegArgumentsBuilder.OutputFrameRate(Pal, profile));
    }

    [Fact]
    public void InterlacedAllowProcessing_Bff_UsesYadifParityBff()
    {
        var bff = Pal with { TopFieldFirst = false };
        var profile = RecordingProfile.CreateFfv1Archival() with { AllowProcessing = true };
        var args = Build(profile, video: bff, audio: Pcm48k, pipe: @"\\.\pipe\a");

        Assert.True(ContainsPair(args, "-vf", "setfield=bff,yadif=1:1:0"));
        Assert.True(ContainsPair(args, "-field_order", "progressive"));
    }

    // ---- H.264 / H.265 / ProRes ------------------------------------------------

    [Fact]
    public void H264_UsesCrfPresetGopProfileAndFaststart()
    {
        var profile = RecordingProfile.CreateH264Access();
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mp4");

        Assert.True(ContainsPair(args, "-c:v", "libx264"));
        Assert.True(ContainsPair(args, "-crf", profile.Crf.ToString()));
        Assert.True(ContainsPair(args, "-preset", profile.Preset));
        Assert.True(ContainsPair(args, "-g", profile.GopSize.ToString()));
        Assert.True(ContainsPair(args, "-profile:v", "high"));
        Assert.True(ContainsPair(args, "-c:a", "aac"));
        Assert.True(ContainsPair(args, "-b:a", "192k"));
        Assert.True(ContainsPair(args, "-movflags", "+faststart"));
    }

    [Fact]
    public void H264_InterlacedSource_EnablesInterlacedEncodingFlags()
    {
        var args = Build(RecordingProfile.CreateH264Access(), audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-vf", "setfield=tff"));
        Assert.True(ContainsPair(args, "-field_order", "tt"));
        Assert.True(ContainsPair(args, "-flags", "+ildct+ilme"));
    }

    [Fact]
    public void H265_UsesLibx265WithCrf()
    {
        var profile = RecordingProfile.CreateH265Access();
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mp4");

        Assert.True(ContainsPair(args, "-c:v", "libx265"));
        Assert.True(ContainsPair(args, "-crf", "28"));
        Assert.True(ContainsPair(args, "-preset", "medium"));
        Assert.True(ContainsPair(args, "-c:a", "aac"));
        Assert.True(ContainsPair(args, "-profile:v", "main"));
        Assert.True(ContainsPair(args, "-pix_fmt", "yuv420p"));
    }

    [Fact]
    public void H265_MainWithYuv422p_OmitsProfileSoX265PicksFromPixFmt()
    {
        // -profile:v main fails (i422 incompatible); -profile:v main422-8 also fails
        // on current FFmpeg/x265 ("unknown profile"). Omit profile; keep yuv422p.
        var profile = RecordingProfile.CreateH265Access() with
        {
            PixelFormat = "yuv422p",
            VideoProfile = "main",
            Container = "mkv",
            AudioCodec = RecordingAudioCodec.PcmS16Le,
        };
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mkv");

        Assert.True(ContainsPair(args, "-pix_fmt", "yuv422p"));
        Assert.DoesNotContain("-profile:v", args);
    }

    [Fact]
    public void H265_Yuv422p10_OmitsProfile()
    {
        var profile = RecordingProfile.CreateH265Access() with
        {
            PixelFormat = "yuv422p10le",
            VideoProfile = "main10",
        };
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-pix_fmt", "yuv422p10le"));
        Assert.DoesNotContain("-profile:v", args);
    }

    [Fact]
    public void H264_HighWithYuv422p_UpgradesToHigh422()
    {
        var profile = RecordingProfile.CreateH264Access() with { PixelFormat = "yuv422p" };
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-profile:v", "high422"));
    }

    [Fact]
    public void H264_TuneNone_OmitsTuneFlag()
    {
        var profile = RecordingProfile.CreateH264Access() with { Tune = "" };
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.DoesNotContain("-tune", args);
    }

    [Fact]
    public void ProRes_UsesProresKsWithProfileAnd10Bit()
    {
        var profile = RecordingProfile.CreateProResHq();
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mov");

        Assert.True(ContainsPair(args, "-c:v", "prores_ks"));
        Assert.True(ContainsPair(args, "-profile:v", profile.ProResProfile.ToString()));
        Assert.True(ContainsPair(args, "-pix_fmt", "yuv422p10le"));
        Assert.True(ContainsPair(args, "-c:a", "pcm_s16le"));
        Assert.DoesNotContain("-movflags", args);
    }

    [Fact]
    public void PixelFormatOverride_IsHonored()
    {
        var profile = RecordingProfile.CreateH264Access() with { PixelFormat = "yuv422p" };
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a");
        Assert.True(ContainsPair(args, "-pix_fmt", "yuv422p"));
    }

    // ---- Inputs -----------------------------------------------------------------

    [Fact]
    public void VideoInput_DescribesRawFramesOnStdin()
    {
        var args = Build(RecordingProfile.CreateFfv1Archival(), audio: Pcm48k, pipe: @"\\.\pipe\a");

        Assert.True(ContainsPair(args, "-f", "rawvideo"));
        Assert.True(ContainsPair(args, "-pix_fmt", "bgr24"));
        Assert.True(ContainsPair(args, "-video_size", "720x576"));
        Assert.True(ContainsPair(args, "-framerate", "25"));
        Assert.True(ContainsPair(args, "-i", "pipe:0"));
    }

    [Fact]
    public void AudioInput_DescribesPcmPipe()
    {
        var args = Build(RecordingProfile.CreateFfv1Archival(), audio: Pcm48k, pipe: @"\\.\pipe\a");

        Assert.True(ContainsPair(args, "-f", "s16le"));
        Assert.True(ContainsPair(args, "-ar", "48000"));
        Assert.True(ContainsPair(args, "-ac", "2"));
        Assert.True(ContainsPair(args, "-i", @"\\.\pipe\a"));
    }

    [Fact]
    public void AvInputs_AudioPipeBeforeVideoStdin()
    {
        // Audio-first open order lets the recorder align pumps without stdin probe deadlock.
        var args = Build(RecordingProfile.CreateFfv1Archival(), audio: Pcm48k, pipe: @"\\.\pipe\a");
        var audioIn = args.ToList().IndexOf(@"\\.\pipe\a");
        var videoIn = args.ToList().IndexOf("pipe:0");
        Assert.True(audioIn > 0 && videoIn > 0);
        Assert.True(audioIn < videoIn);
    }

    [Fact]
    public void VideoOnly_OmitsAudioArguments()
    {
        var args = Build(RecordingProfile.CreateFfv1Archival());
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("-ar", args);
    }

    [Fact]
    public void AudioWithoutPipePath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            FfmpegArgumentsBuilder.Build(
                RecordingProfile.CreateFfv1Archival(), Pal, Pcm48k, null, "out.mkv"));
    }

    // ---- Audio codecs ------------------------------------------------------------

    [Fact]
    public void Aac_UsesConfiguredBitrate()
    {
        var profile = RecordingProfile.CreateH264Access() with { AudioBitrateKbps = 256 };
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mp4");
        Assert.True(ContainsPair(args, "-c:a", "aac"));
        Assert.True(ContainsPair(args, "-b:a", "256k"));
    }

    [Fact]
    public void Mp3_UsesLibmp3lameAndBitrate()
    {
        var profile = RecordingProfile.CreateH264Access() with
        {
            Container = "mkv",
            AudioCodec = RecordingAudioCodec.Mp3,
            AudioBitrateKbps = 320,
            Mp4FastStart = false,
        };
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mkv");
        Assert.True(ContainsPair(args, "-c:a", "libmp3lame"));
        Assert.True(ContainsPair(args, "-b:a", "320k"));
    }

    [Fact]
    public void Flac_UsesCompressionLevel()
    {
        var profile = RecordingProfile.CreateFfv1Archival() with
        {
            AudioCodec = RecordingAudioCodec.Flac,
            FlacCompressionLevel = 8,
        };
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mkv");
        Assert.True(ContainsPair(args, "-c:a", "flac"));
        Assert.True(ContainsPair(args, "-compression_level", "8"));
    }

    [Fact]
    public void Vorbis_UsesQuality()
    {
        var profile = RecordingProfile.CreateFfv1Archival() with
        {
            AudioCodec = RecordingAudioCodec.Vorbis,
            AudioQuality = 7,
        };
        var args = Build(profile, audio: Pcm48k, pipe: @"\\.\pipe\a", output: "out.mkv");
        Assert.True(ContainsPair(args, "-c:a", "libvorbis"));
        Assert.True(ContainsPair(args, "-q:a", "7"));
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
