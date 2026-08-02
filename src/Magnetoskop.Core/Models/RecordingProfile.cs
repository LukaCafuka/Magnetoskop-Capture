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
}

/// <summary>
/// Recording encode settings: video codec + container + advanced options, plus the
/// audio codec chosen until a dedicated Audio settings window exists.
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

    /// <summary>Human-readable summary for the UI (e.g. "H.264 / MP4 (CRF 18)").</summary>
    public string DisplayName => BuildDisplayName();

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

    /// <summary>Picks a sensible audio codec for the video codec / container pair.</summary>
    public static RecordingAudioCodec DefaultAudioCodec(RecordingCodec video, string container)
    {
        var c = container.ToLowerInvariant();
        return video switch
        {
            RecordingCodec.Ffv1 => RecordingAudioCodec.PcmS24Le,
            RecordingCodec.ProRes => RecordingAudioCodec.PcmS16Le,
            RecordingCodec.H264 or RecordingCodec.H265 when c is "mp4" or "mov"
                => RecordingAudioCodec.Aac,
            _ => RecordingAudioCodec.PcmS16Le,
        };
    }

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
