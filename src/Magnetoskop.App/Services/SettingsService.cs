using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.App.Services;

/// <summary>Persisted video encode options (maps to <see cref="RecordingProfile"/>).</summary>
public sealed class VideoEncodeSettings
{
    public string Container { get; set; } = "mp4";
    public string VideoCodec { get; set; } = nameof(RecordingCodec.H264);
    public int Crf { get; set; } = 18;
    public string Preset { get; set; } = "medium";
    public string Tune { get; set; } = "";
    public string VideoProfile { get; set; } = "high";
    public int GopSize { get; set; } = 250;
    public int ProResProfile { get; set; } = 3;
    public int Ffv1Level { get; set; } = 3;
    public int Ffv1Slices { get; set; } = 24;
    public bool Ffv1SliceCrc { get; set; } = true;
    public string PixelFormat { get; set; } = "";
    public bool AllowProcessing { get; set; }
    public bool Mp4FastStart { get; set; } = true;

    public static VideoEncodeSettings FromProfile(RecordingProfile profile) => new()
    {
        Container = profile.Container,
        VideoCodec = profile.VideoCodec.ToString(),
        Crf = profile.Crf,
        Preset = profile.Preset,
        Tune = profile.Tune,
        VideoProfile = profile.VideoProfile,
        GopSize = profile.GopSize,
        ProResProfile = profile.ProResProfile,
        Ffv1Level = profile.Ffv1Level,
        Ffv1Slices = profile.Ffv1Slices,
        Ffv1SliceCrc = profile.Ffv1SliceCrc,
        PixelFormat = profile.PixelFormat,
        AllowProcessing = profile.AllowProcessing,
        Mp4FastStart = profile.Mp4FastStart,
    };

    public RecordingProfile ToProfile()
    {
        if (!Enum.TryParse<RecordingCodec>(VideoCodec, ignoreCase: true, out var codec))
        {
            codec = RecordingCodec.H264;
        }

        var container = string.IsNullOrWhiteSpace(Container)
            ? RecordingProfile.PreferredContainer(codec)
            : Container.ToLowerInvariant();

        if (!RecordingProfile.IsCompatible(codec, container))
        {
            container = RecordingProfile.PreferredContainer(codec);
        }

        return new RecordingProfile
        {
            Id = $"custom-{codec.ToString().ToLowerInvariant()}-{container}",
            VideoCodec = codec,
            AudioCodec = RecordingProfile.DefaultAudioCodec(codec, container),
            Container = container,
            Crf = Crf,
            Preset = string.IsNullOrWhiteSpace(Preset) ? "medium" : Preset,
            Tune = Tune ?? "",
            VideoProfile = VideoProfile ?? "",
            GopSize = GopSize > 0 ? GopSize : RecordingProfile.DefaultGop(codec),
            ProResProfile = ProResProfile,
            Ffv1Level = Ffv1Level is 1 or 3 ? Ffv1Level : 3,
            Ffv1Slices = Ffv1Slices > 0 ? Ffv1Slices : 24,
            Ffv1SliceCrc = Ffv1SliceCrc,
            PixelFormat = PixelFormat ?? "",
            AllowProcessing = AllowProcessing,
            Mp4FastStart = Mp4FastStart,
        };
    }
}

/// <summary>Persisted user settings.</summary>
public sealed class AppSettings
{
    public string? OutputDirectory { get; set; }
    public VideoEncodeSettings? Video { get; set; }
    public string? VideoDeviceId { get; set; }
    public string? AudioDeviceId { get; set; }
    public bool AudioManuallySelected { get; set; }
    public string? VtrConnectionId { get; set; }
    public string? VtrProfileId { get; set; }
    public string? FfmpegPath { get; set; }
    public bool AutoPlayOnRecord { get; set; }

    /// <summary>When true, write a per-instance debug log under {AppBase}/logs/.</summary>
    public bool DebugLoggingEnabled { get; set; }

    /// <summary>When true, show the LOG panel at the bottom of the main window.</summary>
    public bool ShowLogPanel { get; set; }

    /// <summary>Obsolete preset id from earlier builds; ignored on load.</summary>
    public string? RecordingProfileId { get; set; }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON under %AppData%.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _path;

    public SettingsService(ILogger<SettingsService> logger)
        : this(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MagnetoskopCapture", "settings.json"))
    {
    }

    public SettingsService(ILogger<SettingsService> logger, string path)
    {
        _logger = logger;
        _path = path;
    }

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                _logger.LogInformation("Settings loaded from {Path}", _path);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to load settings; using defaults");
            Current = new AppSettings();
        }

        Current.Video ??= VideoEncodeSettings.FromProfile(RecordingProfile.CreateDefault());
        return Current;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Stop writing the obsolete field.
            Current.RecordingProfileId = null;
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
            _logger.LogInformation("Settings saved to {Path}", _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to save settings");
        }
    }
}
