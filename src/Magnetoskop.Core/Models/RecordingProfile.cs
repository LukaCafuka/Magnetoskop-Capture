namespace Magnetoskop.Core.Models;

/// <summary>Video codecs available for recording.</summary>
public enum RecordingCodec
{
    H264,
    H265,
    Ffv1,
    ProRes,
}

/// <summary>Audio codecs available for recording.</summary>
public enum RecordingAudioCodec
{
    PcmS16Le,
    PcmS24Le,
    Aac,
    Flac,
    Mp3,
    Vorbis,
}

/// <summary>
/// Recording encode settings: video codec + container + advanced options, plus
/// user-selected audio codec and tuning (Audio settings window).
/// Consumed by the FFmpeg-based recording service.
/// </summary>
public sealed record RecordingProfile
{
    public required string Id { get; init; }
    public required RecordingCodec VideoCodec { get; init; }
    public required RecordingAudioCodec AudioCodec { get; init; }
    /// <summary>Container/file extension without the dot (avi, mkv, mp4, mov).</summary>
    public required string Container { get; init; }

    /// <summary>H.264 / H.265 CRF (ignored for other codecs).</summary>
    public int Crf { get; init; } = 18;
    /// <summary>libx264 / libx265 preset.</summary>
    public string Preset { get; init; } = "medium";
    /// <summary>libx264 / libx265 tune; empty or "none" omits the flag.</summary>
    public string Tune { get; init; } = "";
    /// <summary>Encoder profile (e.g. high, main); empty omits the flag.</summary>
    public string VideoProfile { get; init; } = "";
    /// <summary>GOP size (-g). FFV1 archival defaults to 1.</summary>
    public int GopSize { get; init; } = 250;
    /// <summary>ProRes profile index for prores_ks: 0=Proxy 1=LT 2=Standard 3=HQ 4=4444.</summary>
    public int ProResProfile { get; init; } = 3;
    /// <summary>FFV1 level (1 or 3).</summary>
    public int Ffv1Level { get; init; } = 3;
    /// <summary>FFV1 slice count.</summary>
    public int Ffv1Slices { get; init; } = 24;
    /// <summary>FFV1 per-slice CRC.</summary>
    public bool Ffv1SliceCrc { get; init; } = true;
    /// <summary>Output pixel format override; empty = codec default.</summary>
    public string PixelFormat { get; init; } = "";
    /// <summary>When false (default), interlacing and field order are preserved untouched.</summary>
    public bool AllowProcessing { get; init; }
    /// <summary>Write moov atom at the start for MP4/MOV progressive download.</summary>
    public bool Mp4FastStart { get; init; } = true;

    /// <summary>AAC / MP3 bitrate in kbps (ignored for other audio codecs).</summary>
    public int AudioBitrateKbps { get; init; } = 192;
    /// <summary>Vorbis <c>-q:a</c> quality 0–10 (ignored for other audio codecs).</summary>
    public int AudioQuality { get; init; } = 5;
    /// <summary>FLAC compression level 0–12 (ignored for other audio codecs).</summary>
    public int FlacCompressionLevel { get; init; } = 5;

    /// <summary>Human-readable summary for the UI (e.g. "H.264 / MP4 (CRF 18)").</summary>
    public string DisplayName => BuildDisplayName();

    /// <summary>Short label for the audio codec (e.g. "AAC", "PCM 24").</summary>
    public string AudioCodecDisplayName => FormatAudioCodec(AudioCodec);

    public static RecordingProfile CreateDefault() => CreateH264Access();

    public static RecordingProfile CreateH264Access() => new()
    {
        Id = "h264-access",
        VideoCodec = RecordingCodec.H264,
        AudioCodec = RecordingAudioCodec.Aac,
        Container = "mp4",
        Crf = 18,
        Preset = "medium",
        VideoProfile = "high",
        GopSize = 250,
        Mp4FastStart = true,
        AudioBitrateKbps = 192,
    };

    public static RecordingProfile CreateH265Access() => new()
    {
        Id = "h265-access",
        VideoCodec = RecordingCodec.H265,
        AudioCodec = RecordingAudioCodec.Aac,
        Container = "mp4",
        Crf = 28,
        Preset = "medium",
        VideoProfile = "main",
        GopSize = 250,
        Mp4FastStart = true,
        AudioBitrateKbps = 192,
    };

    public static RecordingProfile CreateFfv1Archival() => new()
    {
        Id = "ffv1-archival",
        VideoCodec = RecordingCodec.Ffv1,
        AudioCodec = RecordingAudioCodec.PcmS24Le,
        Container = "mkv",
        GopSize = 1,
        Ffv1Level = 3,
        Ffv1Slices = 24,
        Ffv1SliceCrc = true,
    };

    public static RecordingProfile CreateProResHq() => new()
    {
        Id = "prores-hq",
        VideoCodec = RecordingCodec.ProRes,
        AudioCodec = RecordingAudioCodec.PcmS16Le,
        Container = "mov",
        ProResProfile = 3,
        Mp4FastStart = false,
    };

    /// <summary>Preferred container for a codec when the current one is incompatible.</summary>
    public static string PreferredContainer(RecordingCodec codec) => codec switch
    {
        RecordingCodec.Ffv1 => "mkv",
        RecordingCodec.ProRes => "mov",
        _ => "mp4",
    };

    public static bool IsCompatible(RecordingCodec codec, string container)
    {
        var c = container.ToLowerInvariant();
        return codec switch
        {
            RecordingCodec.Ffv1 => c is "mkv" or "avi",
            RecordingCodec.ProRes => c is "mov" or "mkv",
            RecordingCodec.H264 or RecordingCodec.H265 => c is "mp4" or "mkv" or "mov" or "avi",
            _ => false,
        };
    }

    /// <summary>
    /// Whether <paramref name="audio"/> can be muxed into <paramref name="container"/>.
    /// PCM 16 and 24 share the same container rules.
    /// </summary>
    public static bool IsAudioCompatible(RecordingAudioCodec audio, string container)
    {
        var c = container.ToLowerInvariant();
        return audio switch
        {
            RecordingAudioCodec.Aac => c is "mkv" or "mp4" or "mov",
            RecordingAudioCodec.Flac => c is "mkv",
            RecordingAudioCodec.PcmS16Le or RecordingAudioCodec.PcmS24Le => c is "avi" or "mkv" or "mov",
            RecordingAudioCodec.Mp3 => c is "avi" or "mkv" or "mp4" or "mov",
            RecordingAudioCodec.Vorbis => c is "mkv",
            _ => false,
        };
    }

    /// <summary>True when either PCM bit depth is compatible with the container.</summary>
    public static bool IsPcmCompatible(string container)
        => IsAudioCompatible(RecordingAudioCodec.PcmS16Le, container);

    /// <summary>
    /// Fallback audio when the current choice cannot mux into the container,
    /// preferring archival PCM for FFV1/ProRes and AAC for delivery containers.
    /// </summary>
    public static RecordingAudioCodec DefaultAudioCodec(RecordingCodec video, string container)
    {
        var c = container.ToLowerInvariant();
        var preferred = video switch
        {
            RecordingCodec.Ffv1 => RecordingAudioCodec.PcmS24Le,
            RecordingCodec.ProRes => RecordingAudioCodec.PcmS16Le,
            RecordingCodec.H264 or RecordingCodec.H265 when c is "mp4" or "mov"
                => RecordingAudioCodec.Aac,
            _ => RecordingAudioCodec.PcmS16Le,
        };

        if (IsAudioCompatible(preferred, c))
            return preferred;

        return PreferredAudioCodec(c);
    }

    /// <summary>First mux-legal audio codec for a container (used when coercing).</summary>
    public static RecordingAudioCodec PreferredAudioCodec(string container)
    {
        var c = container.ToLowerInvariant();
        return c switch
        {
            "mp4" => RecordingAudioCodec.Aac,
            "mov" => RecordingAudioCodec.PcmS16Le,
            "avi" => RecordingAudioCodec.PcmS16Le,
            "mkv" => RecordingAudioCodec.PcmS16Le,
            _ => RecordingAudioCodec.Aac,
        };
    }

    /// <summary>
    /// Keeps <paramref name="audio"/> when still mux-legal; otherwise returns a default
    /// for the video/container pair.
    /// </summary>
    public static RecordingAudioCodec CoerceAudioCodec(
        RecordingAudioCodec audio, RecordingCodec video, string container)
        => IsAudioCompatible(audio, container) ? audio : DefaultAudioCodec(video, container);

    public static int DefaultCrf(RecordingCodec codec) => codec switch
    {
        RecordingCodec.H265 => 28,
        _ => 18,
    };

    public static int DefaultGop(RecordingCodec codec) => codec switch
    {
        RecordingCodec.Ffv1 => 1,
        _ => 250,
    };

    public static string FormatAudioCodec(RecordingAudioCodec audio) => audio switch
    {
        RecordingAudioCodec.Aac => "AAC",
        RecordingAudioCodec.Flac => "FLAC",
        RecordingAudioCodec.Mp3 => "MP3",
        RecordingAudioCodec.Vorbis => "Vorbis",
        RecordingAudioCodec.PcmS16Le => "PCM 16",
        RecordingAudioCodec.PcmS24Le => "PCM 24",
        _ => audio.ToString(),
    };

    private string BuildDisplayName()
    {
        var codec = VideoCodec switch
        {
            RecordingCodec.H264 => "H.264",
            RecordingCodec.H265 => "H.265",
            RecordingCodec.Ffv1 => "FFV1",
            RecordingCodec.ProRes => ProResProfile switch
            {
                0 => "ProRes Proxy",
                1 => "ProRes LT",
                2 => "ProRes",
                3 => "ProRes HQ",
                4 => "ProRes 4444",
                _ => "ProRes",
            },
            _ => VideoCodec.ToString(),
        };
        var container = Container.ToUpperInvariant();
        var detail = VideoCodec switch
        {
            RecordingCodec.H264 or RecordingCodec.H265 => $"CRF {Crf}",
            RecordingCodec.Ffv1 => $"level {Ffv1Level}",
            _ => null,
        };
        return detail is null ? $"{codec} / {container}" : $"{codec} / {container} ({detail})";
    }
}
