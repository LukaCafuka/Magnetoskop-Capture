using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.App.Services;

/// <summary>Persisted user settings.</summary>
public sealed class AppSettings
{
    public string? OutputDirectory { get; set; }
    public string? RecordingProfileId { get; set; }
    public string? VideoDeviceId { get; set; }
    public string? AudioDeviceId { get; set; }
    public bool AudioManuallySelected { get; set; }
    public string? VtrConnectionId { get; set; }
    public string? VtrProfileId { get; set; }
    public string? FfmpegPath { get; set; }
    public bool AutoPlayOnRecord { get; set; }
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
        return Current;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
            _logger.LogInformation("Settings saved to {Path}", _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to save settings");
        }
    }
}