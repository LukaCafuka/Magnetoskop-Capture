using Magnetoskop.Core.Models;

namespace Magnetoskop.Recording;

/// <summary>
/// Builds FFmpeg command lines for the recording profiles.
/// When audio is present it is listed before video so FFmpeg opens the named
/// pipe first; video then enters on stdin (rawvideo). PCM audio streams over
/// a Windows named pipe. No OpenCV VideoWriter limitations apply.
/// </summary>
public static class FfmpegArgumentsBuilder
{
    /// <summary>
    /// Composes the full argument list.
    /// </summary>
    public static IReadOnlyList<string> Build(
        RecordingProfile profile,
        VideoFormat videoFormat,
        AudioFormat? audioFormat,
        string? audioPipePath,
        string outputPath)
    {
        if (audioFormat is not null && string.IsNullOrEmpty(audioPipePath))
        {
            throw new ArgumentException("Audio pipe path is required when audio is recorded.",
                nameof(audioPipePath));
        }

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
        };

        // Audio before video when both are present: FFmpeg opens inputs in order.
        // Opening the named pipe first lets the recorder WaitForConnection, drain
        // capture backlog, and start A/V pumps together — without feeding stdin
        // during probe (which would otherwise be required and desync the streams).
        // probesize/analyzeduration must be tiny: defaults would buffer ~5 s of PCM
        // before opening the video input, stalling stdin writes.
        if (audioFormat is not null)
        {
            args.AddRange(new[]
            {
                "-fflags", "nobuffer",
                "-probesize", "32",
                "-analyzeduration", "0",
                "-f", PcmInputFormat(audioFormat),
                "-ar", audioFormat.SampleRate.ToString(),
                "-ac", audioFormat.Channels.ToString(),
                "-i", audioPipePath!,
            });
        }

        // ---- Video input: raw frames on stdin ----
        args.AddRange(new[]
        {
            "-fflags", "nobuffer",
            "-probesize", "32",
            "-analyzeduration", "0",
            "-f", "rawvideo",
            "-pix_fmt", PixelFormatName(videoFormat.PixelFormat),
            "-video_size", $"{videoFormat.Width}x{videoFormat.Height}",
            "-framerate", FormatFrameRate(videoFormat.FrameRate),
            "-i", "pipe:0",
        });

        // Scan handling: preserve field structure, or yadif 2× deinterlace when processing is allowed.
        // setfield is required so HEVC/FFV1/ProRes actually carry scan metadata (field_order alone is not enough).
        var (videoFilter, fieldOrder) = BuildVideoFilter(videoFormat, profile.AllowProcessing);
        args.AddRange(new[] { "-vf", videoFilter });

        // ---- Video codec ----
        switch (profile.VideoCodec)
        {
            case RecordingCodec.Ffv1:
                args.AddRange(new[]
                {
                    "-c:v", "ffv1",
                    "-level", profile.Ffv1Level.ToString(),
                    "-g", profile.GopSize.ToString(),
                    "-slices", profile.Ffv1Slices.ToString(),
                    "-slicecrc", profile.Ffv1SliceCrc ? "1" : "0",
                    "-pix_fmt", OutputPixFmt(profile, "yuv422p"),
                });
                break;

            case RecordingCodec.H264:
            {
                var pixFmt = OutputPixFmt(profile, "yuv420p");
                args.AddRange(new[]
                {
                    "-c:v", "libx264",
                    "-crf", profile.Crf.ToString(),
                    "-preset", profile.Preset,
                    "-g", profile.GopSize.ToString(),
                    "-pix_fmt", pixFmt,
                });
                AppendOptional(args, "-tune", profile.Tune);
                AppendOptional(args, "-profile:v", ResolveAvcProfile(profile.VideoProfile, pixFmt));
                break;
            }

            case RecordingCodec.H265:
            {
                var pixFmt = OutputPixFmt(profile, "yuv420p");
                args.AddRange(new[]
                {
                    "-c:v", "libx265",
                    "-crf", profile.Crf.ToString(),
                    "-preset", profile.Preset,
                    "-g", profile.GopSize.ToString(),
                    "-pix_fmt", pixFmt,
                });
                AppendOptional(args, "-tune", profile.Tune);
                AppendOptional(args, "-profile:v", ResolveHevcProfile(profile.VideoProfile, pixFmt));
                break;
            }

            case RecordingCodec.ProRes:
                args.AddRange(new[]
                {
                    "-c:v", "prores_ks",
                    "-profile:v", profile.ProResProfile.ToString(),
                    "-pix_fmt", OutputPixFmt(profile, "yuv422p10le"),
                });
                break;

            default:
                throw new NotSupportedException($"Video codec {profile.VideoCodec} is not supported.");
        }

        // Explicit container/bitstream field order (always — progressive or interlaced).
        args.AddRange(new[] { "-field_order", fieldOrder });

        // H.264 MBAFF-style interlaced coding tools when preserving interlacing.
        if (videoFormat.Interlaced && !profile.AllowProcessing
            && profile.VideoCodec == RecordingCodec.H264)
        {
            args.AddRange(new[] { "-flags", "+ildct+ilme" });
        }

        // ---- Audio codec ----
        if (audioFormat is not null)
        {
            switch (profile.AudioCodec)
            {
                case RecordingAudioCodec.PcmS16Le:
                    args.AddRange(new[] { "-c:a", "pcm_s16le" });
                    break;
                case RecordingAudioCodec.PcmS24Le:
                    args.AddRange(new[] { "-c:a", "pcm_s24le" });
                    break;
                case RecordingAudioCodec.Aac:
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                    break;
                default:
                    throw new NotSupportedException($"Audio codec {profile.AudioCodec} is not supported.");
            }
        }

        // ---- Container specifics ----
        var container = profile.Container.ToLowerInvariant();
        if (profile.Mp4FastStart && container is "mp4" or "mov")
        {
            args.AddRange(new[] { "-movflags", "+faststart" });
        }

        args.Add(outputPath);
        return args;
    }

    /// <summary>
    /// Builds the <c>-vf</c> chain and output <c>-field_order</c>.
    /// When <paramref name="allowProcessing"/> is true on interlaced sources, applies
    /// yadif mode=1 (send one frame per field → 2× frame rate) and tags progressive.
    /// </summary>
    internal static (string VideoFilter, string FieldOrder) BuildVideoFilter(
        VideoFormat format, bool allowProcessing)
    {
        if (!format.Interlaced)
            return ("setfield=prog", "progressive");

        var setfield = format.TopFieldFirst ? "tff" : "bff";
        var parity = format.TopFieldFirst ? "0" : "1"; // yadif: 0=tff, 1=bff

        if (allowProcessing)
        {
            // mode=1 (send_field): one progressive frame per field → 2× fps (e.g. 25i → 50p).
            return ($"setfield={setfield},yadif=1:{parity}:0", "progressive");
        }

        return ($"setfield={setfield}", format.TopFieldFirst ? "tt" : "bb");
    }

    /// <summary>True when the profile will deinterlace an interlaced source (yadif 2×).</summary>
    public static bool WillDeinterlace(VideoFormat format, RecordingProfile profile)
        => format.Interlaced && profile.AllowProcessing;

    /// <summary>Output frame rate after optional yadif 2× (doubled when deinterlacing).</summary>
    public static double OutputFrameRate(VideoFormat format, RecordingProfile profile)
        => WillDeinterlace(format, profile) ? format.FrameRate * 2.0 : format.FrameRate;

    private static string OutputPixFmt(RecordingProfile profile, string codecDefault)
        => string.IsNullOrWhiteSpace(profile.PixelFormat) ? codecDefault : profile.PixelFormat;

    /// <summary>
    /// libx265 Main / Main10 are 4:2:0 only. For 4:2:2 / 4:4:4, omit <c>-profile:v</c>:
    /// FFmpeg's profile flag rejects names like <c>main422-8</c> on current x265 builds
    /// ("unknown profile"), while the encoder correctly picks the profile from <c>-pix_fmt</c>.
    /// </summary>
    internal static string ResolveHevcProfile(string? requested, string pixFmt)
    {
        var chroma = ChromaFamily(pixFmt);

        // Non-4:2:0: never pass -profile:v; x265 derives Main 4:2:2 / 4:4:4 from pix_fmt.
        if (chroma != Chroma.C420)
            return "";

        if (string.IsNullOrWhiteSpace(requested))
            return Is10Bit(pixFmt) ? "main10" : "main";

        var profile = requested.Trim().ToLowerInvariant();
        // Hyphenated 422/444 names also fail via -profile:v; drop them for 4:2:0 misuse too.
        if (profile.Contains("422", StringComparison.Ordinal) ||
            profile.Contains("444", StringComparison.Ordinal))
        {
            return "";
        }

        return requested.Trim();
    }

    /// <summary>
    /// libx264 High / Main / Baseline are 4:2:0. Use high422 / high444 when needed.
    /// </summary>
    internal static string ResolveAvcProfile(string? requested, string pixFmt)
    {
        var chroma = ChromaFamily(pixFmt);
        var bit10 = Is10Bit(pixFmt);

        if (string.IsNullOrWhiteSpace(requested))
        {
            return chroma switch
            {
                Chroma.C444 => "high444",
                Chroma.C422 => "high422",
                _ => bit10 ? "high10" : "high",
            };
        }

        var profile = requested.Trim().ToLowerInvariant();
        if (profile is "baseline" or "main" or "high" or "high10")
        {
            return chroma switch
            {
                Chroma.C444 => "high444",
                Chroma.C422 => "high422",
                _ => profile == "high" && bit10 ? "high10" : profile,
            };
        }

        return requested.Trim();
    }

    private enum Chroma { C420, C422, C444 }

    private static Chroma ChromaFamily(string pixFmt)
    {
        var p = pixFmt.ToLowerInvariant();
        if (p.Contains("444")) return Chroma.C444;
        if (p.Contains("422")) return Chroma.C422;
        return Chroma.C420;
    }

    private static bool Is10Bit(string pixFmt)
    {
        var p = pixFmt.ToLowerInvariant();
        return p.Contains("10") || p.Contains("p010") || p.Contains("p210") || p.Contains("p410");
    }

    private static void AppendOptional(List<string> args, string flag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        args.Add(flag);
        args.Add(value);
    }

    public static string PixelFormatName(VideoPixelFormat format) => format switch
    {
        VideoPixelFormat.Bgr24 => "bgr24",
        VideoPixelFormat.Bgra32 => "bgra",
        VideoPixelFormat.Yuv422 => "uyvy422",
        _ => "bgr24",
    };

    /// <summary>PCM input demuxer name for the given capture format.</summary>
    public static string PcmInputFormat(AudioFormat format) => format.BitsPerSample switch
    {
        16 => "s16le",
        24 => "s24le",
        32 => "s32le",
        _ => throw new NotSupportedException($"{format.BitsPerSample}-bit PCM input is not supported."),
    };

    /// <summary>Frame rate as rational for exact PAL/NTSC values.</summary>
    public static string FormatFrameRate(double fps)
    {
        if (Math.Abs(fps - 25.0) < 0.001) return "25";
        if (Math.Abs(fps - 50.0) < 0.001) return "50";
        if (Math.Abs(fps - 30.0) < 0.001) return "30";
        if (Math.Abs(fps - 29.97) < 0.01) return "30000/1001";
        if (Math.Abs(fps - 23.976) < 0.01) return "24000/1001";
        return fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Joins arguments with quoting suitable for Windows process start.</summary>
    public static string Join(IEnumerable<string> args)
        => string.Join(" ", args.Select(a =>
            a.Contains(' ') || a.Contains('"') ? $"\"{a.Replace("\"", "\\\"")}\"" : a));
}
