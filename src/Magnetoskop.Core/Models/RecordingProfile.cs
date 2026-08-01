namespace Magnetoskop.Core.Models;

/// <summary>Video codecs available for recording.</summary>
public enum RecordingCodec
{
    H264,
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
/// A recording profile: codec + container + options. Profiles are consumed by
/// the FFmpeg-based recording service in later phases.
/// </summary>
public sealed record RecordingProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required RecordingCodec VideoCodec { get; init; }
    public required RecordingAudioCodec AudioCodec { get; init; }
    /// <summary>Container/file extension without the dot (mkv, mp4, mov).</summary>
    public required string Container { get; init; }

    /// <summary>H.264 CRF (ignored for other codecs).</summary>
    public int Crf { get; init; } = 18;
    /// <summary>H.264 encoder preset (ignored for other codecs).</summary>
    public string Preset { get; init; } = "medium";
    /// <summary>ProRes profile index for prores_ks: 0=Proxy 1=LT 2=Standard 3=HQ.</summary>
    public int ProResProfile { get; init; } = 3;
    /// <summary>When false (default), interlacing and field order are preserved untouched.</summary>
    public bool AllowProcessing { get; init; }

    public static IReadOnlyList<RecordingProfile> Defaults { get; } = new[]
    {
        new RecordingProfile
        {
            Id = "ffv1-archival",
            DisplayName = "FFV1 + PCM (Matroska, archival)",
            VideoCodec = RecordingCodec.Ffv1,
            AudioCodec = RecordingAudioCodec.PcmS24Le,
            Container = "mkv",
        },
        new RecordingProfile
        {
            Id = "h264-access",
            DisplayName = "H.264 + AAC (MP4, access copy)",
            VideoCodec = RecordingCodec.H264,
            AudioCodec = RecordingAudioCodec.Aac,
            Container = "mp4",
        },
        new RecordingProfile
        {
            Id = "prores-hq",
            DisplayName = "ProRes HQ + PCM (MOV)",
            VideoCodec = RecordingCodec.ProRes,
            AudioCodec = RecordingAudioCodec.PcmS16Le,
            Container = "mov",
        },
    };
}