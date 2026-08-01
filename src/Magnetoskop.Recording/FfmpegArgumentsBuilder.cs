using Magnetoskop.Core.Models;

namespace Magnetoskop.Recording;

/// <summary>
/// Builds FFmpeg command lines for the recording profiles.
/// Video enters on stdin (rawvideo), audio on a named pipe (PCM),
/// so no OpenCV VideoWriter limitations apply.
/// </summary>
public static class FfmpegArgumentsBuilder
{
    /// <summary>
    /// Composes the full argument list.
    /// </summary>
    /// <param name="profile">Recording profile (codec/container/options).</param>
    /// <param name="videoFormat">Format of the raw frames fed on stdin.</param>
    /// <param name="audioFormat">PCM format fed on the audio pipe; null = video only.</param>
    /// <param name="audioPipePath">Named-pipe path for audio (required when audioFormat given).</param>
    /// <param name="outputPath">Destination file.</param>
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

        // ---- Video input: raw frames on stdin ----
        args.AddRange(new[]
        {
            "-f", "rawvideo",
            "-pix_fmt", PixelFormatName(videoFormat.PixelFormat),
            "-video_size", $"{videoFormat.Width}x{videoFormat.Height}",
            "-framerate", FormatFrameRate(videoFormat.FrameRate),
            "-i", "pipe:0",
        });

        // ---- Audio input: PCM on a named pipe ----
        if (audioFormat is not null)
        {
            args.AddRange(new[]
            {
                "-f", PcmInputFormat(audioFormat),
                "-ar", audioFormat.SampleRate.ToString(),
                "-ac", audioFormat.Channels.ToString(),
                "-i", audioPipePath!,
            });
        }

        // ---- Video codec ----
        switch (profile.VideoCodec)
        {
            case RecordingCodec.Ffv1:
                // FFV1 version 3 with per-slice CRCs and intra-only GOP: the
                // archival configuration recommended for video preservation.
                args.AddRange(new[]
                {
                    "-c:v", "ffv1",
                    "-level", "3",
                    "-g", "1",
                    "-slices", "24",
                    "-slicecrc", "1",
                    "-pix_fmt", "yuv422p",
                });
                break;

            case RecordingCodec.H264:
                args.AddRange(new[]
                {
                    "-c:v", "libx264",
                    "-crf", profile.Crf.ToString(),
                    "-preset", profile.Preset,
                    "-pix_fmt", "yuv420p",
                });
                break;

            case RecordingCodec.ProRes:
                args.AddRange(new[]
                {
                    "-c:v", "prores_ks",
                    "-profile:v", profile.ProResProfile.ToString(),
                    "-pix_fmt", "yuv422p10le",
                });
                break;

            default:
                throw new NotSupportedException($"Video codec {profile.VideoCodec} is not supported.");
        }

        // Interlace metadata: when the source is interlaced and the profile forbids
        // processing, tag the field order instead of deinterlacing.
        if (videoFormat.Interlaced && !profile.AllowProcessing)
        {
            args.AddRange(new[]
            {
                "-field_order", videoFormat.TopFieldFirst ? "tt" : "bb",
            });
            if (profile.VideoCodec == RecordingCodec.H264)
            {
                args.AddRange(new[] { "-flags", "+ildct+ilme" });
            }
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
        if (profile.Container.Equals("mp4", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(new[] { "-movflags", "+faststart" });
        }

        args.Add(outputPath);
        return args;
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
        // Exact common rates
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