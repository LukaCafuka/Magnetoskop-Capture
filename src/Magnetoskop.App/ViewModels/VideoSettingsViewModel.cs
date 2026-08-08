using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magnetoskop.Core.Models;

namespace Magnetoskop.App.ViewModels;

public sealed record NamedOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>Editable draft of video encode settings for the Video settings window.</summary>
public sealed partial class VideoSettingsViewModel : ObservableObject
{
    private readonly RecordingProfile _source;

    public static IReadOnlyList<NamedOption> AllContainers { get; } = new[]
    {
        new NamedOption("avi", "AVI"),
        new NamedOption("mkv", "MKV"),
        new NamedOption("mp4", "MP4"),
        new NamedOption("mov", "MOV"),
    };

    public static IReadOnlyList<NamedOption> AllCodecs { get; } = new[]
    {
        new NamedOption(nameof(RecordingCodec.H264), "H.264"),
        new NamedOption(nameof(RecordingCodec.H265), "H.265"),
        new NamedOption(nameof(RecordingCodec.Ffv1), "FFV1"),
        new NamedOption(nameof(RecordingCodec.ProRes), "ProRes HQ"),
        new NamedOption(nameof(RecordingCodec.DnxHd), "DNxHD"),
    };

    public static IReadOnlyList<string> Presets { get; } = new[]
    {
        "ultrafast", "superfast", "veryfast", "faster", "fast",
        "medium", "slow", "slower", "veryslow",
    };

    public static IReadOnlyList<string> Tunes { get; } = new[]
    {
        "none", "film", "animation", "grain", "stillimage", "fastdecode", "zerolatency",
    };

    public static IReadOnlyList<NamedOption> H264Profiles { get; } = new[]
    {
        new NamedOption("", "Auto"),
        new NamedOption("baseline", "Baseline"),
        new NamedOption("main", "Main"),
        new NamedOption("high", "High"),
        new NamedOption("high10", "High 10"),
        new NamedOption("high422", "High 4:2:2"),
        new NamedOption("high444", "High 4:4:4"),
    };

    public static IReadOnlyList<NamedOption> H265Profiles { get; } = new[]
    {
        // For yuv422/yuv444, leave profile on Auto — FFmpeg -profile:v rejects
        // main422-* names on current x265; the encoder follows -pix_fmt instead.
        new NamedOption("", "Auto"),
        new NamedOption("main", "Main (4:2:0)"),
        new NamedOption("main10", "Main 10 (4:2:0)"),
    };

    public static IReadOnlyList<NamedOption> ProResProfiles { get; } = new[]
    {
        new NamedOption("0", "Proxy"),
        new NamedOption("1", "LT"),
        new NamedOption("2", "Standard"),
        new NamedOption("3", "HQ"),
        new NamedOption("4", "4444"),
    };

    public static IReadOnlyList<NamedOption> DnxHdProfiles { get; } = new[]
    {
        new NamedOption("dnxhr_lb", "DNxHR LB"),
        new NamedOption("dnxhr_sq", "DNxHR SQ"),
        new NamedOption("dnxhr_hq", "DNxHR HQ"),
        new NamedOption("dnxhr_hqx", "DNxHR HQX"),
        new NamedOption("dnxhr_444", "DNxHR 444"),
    };

    public static IReadOnlyList<NamedOption> PixelFormats { get; } = new[]
    {
        new NamedOption("", "Codec default"),
        new NamedOption("yuv420p", "yuv420p"),
        new NamedOption("yuv422p", "yuv422p"),
        new NamedOption("yuv422p10le", "yuv422p10le"),
        new NamedOption("yuv444p10le", "yuv444p10le"),
    };

    public static IReadOnlyList<NamedOption> Ffv1Levels { get; } = new[]
    {
        new NamedOption("1", "1"),
        new NamedOption("3", "3"),
    };

    private bool _suppressCompatibility;

    public VideoSettingsViewModel(RecordingProfile source)
    {
        _source = source;
        AvailableContainers = new ObservableCollection<NamedOption>(AllContainers);
        AvailableCodecs = new ObservableCollection<NamedOption>(AllCodecs);

        _suppressCompatibility = true;
        SelectedCodec = AllCodecs.First(c => c.Id == source.VideoCodec.ToString());
        SelectedContainer = AllContainers.FirstOrDefault(c => c.Id == source.Container.ToLowerInvariant())
            ?? AllContainers.First(c => c.Id == RecordingProfile.PreferredContainer(source.VideoCodec));
        Crf = source.Crf;
        Preset = string.IsNullOrWhiteSpace(source.Preset) ? "medium" : source.Preset;
        Tune = string.IsNullOrWhiteSpace(source.Tune) ? "none" : source.Tune;
        SelectedVideoProfile = ResolveVideoProfileOption(source.VideoCodec, source.VideoProfile);
        GopSize = source.GopSize;
        SelectedProResProfile = ProResProfiles.FirstOrDefault(p => p.Id == source.ProResProfile.ToString())
            ?? ProResProfiles.First(p => p.Id == "3");
        SelectedDnxHdProfile = DnxHdProfiles.FirstOrDefault(p => p.Id == source.DnxHdProfile)
            ?? DnxHdProfiles.First(p => p.Id == "dnxhr_hq");
        SelectedFfv1Level = Ffv1Levels.FirstOrDefault(l => l.Id == source.Ffv1Level.ToString())
            ?? Ffv1Levels.First(l => l.Id == "3");
        Ffv1Slices = source.Ffv1Slices;
        Ffv1SliceCrc = source.Ffv1SliceCrc;
        SelectedPixelFormat = PixelFormats.FirstOrDefault(p => p.Id == source.PixelFormat)
            ?? PixelFormats[0];
        PreserveInterlacing = !source.AllowProcessing;
        Mp4FastStart = source.Mp4FastStart;
        _suppressCompatibility = false;

        RefreshCodecPanels();
        RefreshFastStartVisibility();
    }

    public ObservableCollection<NamedOption> AvailableContainers { get; }
    public ObservableCollection<NamedOption> AvailableCodecs { get; }

    [ObservableProperty]
    private NamedOption _selectedContainer = AllContainers[2];

    [ObservableProperty]
    private NamedOption _selectedCodec = AllCodecs[0];

    [ObservableProperty]
    private int _crf = 18;

    [ObservableProperty]
    private string _preset = "medium";

    [ObservableProperty]
    private string _tune = "none";

    [ObservableProperty]
    private NamedOption? _selectedVideoProfile = H264Profiles[^1];

    [ObservableProperty]
    private int _gopSize = 250;

    [ObservableProperty]
    private NamedOption? _selectedProResProfile = ProResProfiles[3];

    [ObservableProperty]
    private NamedOption? _selectedDnxHdProfile = DnxHdProfiles[2];

    [ObservableProperty]
    private NamedOption? _selectedFfv1Level = Ffv1Levels[1];

    [ObservableProperty]
    private int _ffv1Slices = 24;

    [ObservableProperty]
    private bool _ffv1SliceCrc = true;

    [ObservableProperty]
    private NamedOption? _selectedPixelFormat = PixelFormats[0];

    [ObservableProperty]
    private bool _preserveInterlacing = true;

    [ObservableProperty]
    private bool _mp4FastStart = true;

    [ObservableProperty]
    private string _compatibilityNote = "";

    [ObservableProperty]
    private bool _showAvcHevcOptions;

    [ObservableProperty]
    private bool _showFfv1Options;

    [ObservableProperty]
    private bool _showProResOptions;

    [ObservableProperty]
    private bool _showDnxHdOptions;

    [ObservableProperty]
    private bool _showFastStartOption;

    public IReadOnlyList<NamedOption> CurrentVideoProfiles
        => SelectedCodec.Id == nameof(RecordingCodec.H265) ? H265Profiles : H264Profiles;

    public bool DialogAccepted { get; private set; }

    public event EventHandler? RequestClose;

    partial void OnSelectedContainerChanged(NamedOption value)
    {
        if (_suppressCompatibility) return;
        EnsureCompatible(preferAdjustingCodec: false);
        RefreshFastStartVisibility();
    }

    partial void OnSelectedCodecChanged(NamedOption value)
    {
        if (_suppressCompatibility) return;
        if (value is null) return;
        ApplyCodecDefaults(ParseCodec(value.Id));
        EnsureCompatible(preferAdjustingCodec: true);
        RefreshCodecPanels();
        OnPropertyChanged(nameof(CurrentVideoProfiles));
        // WPF may clear SelectedVideoProfile when the AVC/HEVC panel collapses;
        // restore a valid option so OK / ToProfile never sees null.
        SelectedVideoProfile = ResolveVideoProfileOption(
            ParseCodec(value.Id), SelectedVideoProfile?.Id ?? "");
        EnsureNamedOptionSelections();
    }

    private void ApplyCodecDefaults(RecordingCodec codec)
    {
        Crf = RecordingProfile.DefaultCrf(codec);
        GopSize = RecordingProfile.DefaultGop(codec);
        if (codec is RecordingCodec.H264)
        {
            SelectedVideoProfile = H264Profiles.First(p => p.Id == "high");
        }
        else if (codec is RecordingCodec.H265)
        {
            SelectedVideoProfile = H265Profiles.First(p => p.Id == "main");
        }
        if (codec is RecordingCodec.ProRes)
        {
            SelectedProResProfile = ProResProfiles.First(p => p.Id == "3");
        }
        if (codec is RecordingCodec.DnxHd)
        {
            SelectedDnxHdProfile = DnxHdProfiles.First(p => p.Id == "dnxhr_hq");
        }
        if (codec is RecordingCodec.Ffv1)
        {
            SelectedFfv1Level = Ffv1Levels.First(l => l.Id == "3");
        }
    }

    private void EnsureCompatible(bool preferAdjustingCodec)
    {
        var codec = ParseCodec(SelectedCodec.Id);
        var container = SelectedContainer.Id;
        if (RecordingProfile.IsCompatible(codec, container))
        {
            CompatibilityNote = "";
            return;
        }

        if (preferAdjustingCodec)
        {
            var preferred = RecordingProfile.PreferredContainer(codec);
            _suppressCompatibility = true;
            SelectedContainer = AllContainers.First(c => c.Id == preferred);
            _suppressCompatibility = false;
            CompatibilityNote = $"Container switched to {preferred.ToUpperInvariant()} for {SelectedCodec.DisplayName}.";
            RefreshFastStartVisibility();
        }
        else
        {
            // Prefer keeping the container: pick a codec that supports it.
            var fallback = container switch
            {
                "avi" => nameof(RecordingCodec.H264),
                "mov" => nameof(RecordingCodec.ProRes),
                "mkv" => nameof(RecordingCodec.Ffv1),
                _ => nameof(RecordingCodec.H264),
            };
            // If container is mov and user had h264, don't force ProRes — H264 works with mov.
            // This branch only runs when incompatible, so for mov+ffv1 → prores or h264.
            if (container == "mov" && codec == RecordingCodec.Ffv1)
            {
                fallback = nameof(RecordingCodec.H264);
            }
            if (container == "mp4" && codec is RecordingCodec.Ffv1 or RecordingCodec.ProRes or RecordingCodec.DnxHd)
            {
                fallback = nameof(RecordingCodec.H264);
            }
            if (container == "avi" && codec is RecordingCodec.ProRes or RecordingCodec.DnxHd)
            {
                fallback = nameof(RecordingCodec.H264);
            }

            _suppressCompatibility = true;
            SelectedCodec = AllCodecs.First(c => c.Id == fallback);
            ApplyCodecDefaults(ParseCodec(fallback));
            _suppressCompatibility = false;
            CompatibilityNote = $"Codec switched to {SelectedCodec.DisplayName} for {SelectedContainer.DisplayName}.";
            RefreshCodecPanels();
        }
    }

    private void RefreshCodecPanels()
    {
        var codec = ParseCodec(SelectedCodec.Id);
        ShowAvcHevcOptions = codec is RecordingCodec.H264 or RecordingCodec.H265;
        ShowFfv1Options = codec is RecordingCodec.Ffv1;
        ShowProResOptions = codec is RecordingCodec.ProRes;
        ShowDnxHdOptions = codec is RecordingCodec.DnxHd;
    }

    private void RefreshFastStartVisibility()
    {
        ShowFastStartOption = SelectedContainer.Id is "mp4" or "mov";
    }

    [RelayCommand]
    private void Ok()
    {
        DialogAccepted = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogAccepted = false;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    public RecordingProfile ToProfile()
    {
        EnsureNamedOptionSelections();

        var codec = ParseCodec(SelectedCodec.Id);
        var container = SelectedContainer.Id;
        if (!RecordingProfile.IsCompatible(codec, container))
        {
            container = RecordingProfile.PreferredContainer(codec);
        }

        var audio = RecordingProfile.CoerceAudioCodec(_source.AudioCodec, codec, container);

        return new RecordingProfile
        {
            Id = $"custom-{codec.ToString().ToLowerInvariant()}-{container}",
            VideoCodec = codec,
            AudioCodec = audio,
            Container = container,
            Crf = Math.Clamp(Crf, 0, 51),
            Preset = Preset,
            Tune = Tune == "none" ? "" : Tune,
            VideoProfile = SelectedVideoProfile?.Id ?? "",
            GopSize = Math.Max(1, GopSize),
            ProResProfile = int.TryParse(SelectedProResProfile?.Id, out var pr) ? pr : 3,
            DnxHdProfile = SelectedDnxHdProfile?.Id ?? "dnxhr_hq",
            Ffv1Level = int.TryParse(SelectedFfv1Level?.Id, out var level) ? level : 3,
            Ffv1Slices = Math.Max(1, Ffv1Slices),
            Ffv1SliceCrc = Ffv1SliceCrc,
            PixelFormat = SelectedPixelFormat?.Id ?? "",
            AllowProcessing = !PreserveInterlacing,
            Mp4FastStart = Mp4FastStart,
            AudioBitrateKbps = _source.AudioBitrateKbps,
            AudioQuality = _source.AudioQuality,
            FlacCompressionLevel = _source.FlacCompressionLevel,
        };
    }

    /// <summary>
    /// Collapsing codec-specific panels can leave ComboBox SelectedItem null via WPF binding.
    /// Restore defaults so ToProfile never dereferences null NamedOptions.
    /// </summary>
    private void EnsureNamedOptionSelections()
    {
        SelectedVideoProfile ??= ResolveVideoProfileOption(ParseCodec(SelectedCodec.Id), "");
        SelectedProResProfile ??= ProResProfiles.First(p => p.Id == "3");
        SelectedDnxHdProfile ??= DnxHdProfiles.First(p => p.Id == "dnxhr_hq");
        SelectedFfv1Level ??= Ffv1Levels.First(l => l.Id == "3");
        SelectedPixelFormat ??= PixelFormats[0];
        if (string.IsNullOrWhiteSpace(Preset)) Preset = "medium";
        if (string.IsNullOrWhiteSpace(Tune)) Tune = "none";
    }

    private static RecordingCodec ParseCodec(string id)
        => Enum.TryParse<RecordingCodec>(id, out var codec) ? codec : RecordingCodec.H264;

    private static NamedOption ResolveVideoProfileOption(RecordingCodec codec, string profileId)
    {
        var options = codec == RecordingCodec.H265 ? H265Profiles : H264Profiles;
        return options.FirstOrDefault(p => p.Id == profileId) ?? options[0];
    }
}
