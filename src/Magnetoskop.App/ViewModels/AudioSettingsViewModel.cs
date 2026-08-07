using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magnetoskop.Core.Models;

namespace Magnetoskop.App.ViewModels;

/// <summary>Codec row in the Audio settings list (may be disabled for the current container).</summary>
public sealed partial class AudioCodecChoice : ObservableObject
{
    public AudioCodecChoice(string id, string displayName, bool isEnabled)
    {
        Id = id;
        DisplayName = displayName;
        _isEnabled = isEnabled;
    }

    public string Id { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public override string ToString() => DisplayName;
}

/// <summary>Editable draft of audio encode settings for the Audio settings window.</summary>
public sealed partial class AudioSettingsViewModel : ObservableObject
{
    public const string CodecAac = "Aac";
    public const string CodecFlac = "Flac";
    public const string CodecPcm = "Pcm";
    public const string CodecMp3 = "Mp3";
    public const string CodecVorbis = "Vorbis";

    public static IReadOnlyList<int> BitrateOptions { get; } = new[] { 128, 160, 192, 256, 320 };

    public static IReadOnlyList<NamedOption> PcmBitDepths { get; } = new[]
    {
        new NamedOption("16", "16-bit"),
        new NamedOption("24", "24-bit"),
    };

    private readonly RecordingProfile _source;
    private bool _suppressSelectionGuard;

    public AudioSettingsViewModel(RecordingProfile source)
    {
        _source = source;

        var container = source.Container.ToLowerInvariant();
        AvailableCodecs = new ObservableCollection<AudioCodecChoice>
        {
            new(CodecAac, "AAC", RecordingProfile.IsAudioCompatible(RecordingAudioCodec.Aac, container)),
            new(CodecFlac, "FLAC", RecordingProfile.IsAudioCompatible(RecordingAudioCodec.Flac, container)),
            new(CodecPcm, "PCM", RecordingProfile.IsPcmCompatible(container)),
            new(CodecMp3, "MP3", RecordingProfile.IsAudioCompatible(RecordingAudioCodec.Mp3, container)),
            new(CodecVorbis, "Vorbis", RecordingProfile.IsAudioCompatible(RecordingAudioCodec.Vorbis, container)),
        };

        ContextSummary =
            $"{FormatVideo(source.VideoCodec)} · {container.ToUpperInvariant()} — " +
            "disabled codecs cannot be muxed into this container.";

        _suppressSelectionGuard = true;
        SelectedCodec = ResolveInitialCodec(source);
        SelectedBitrateKbps = BitrateOptions.Contains(source.AudioBitrateKbps)
            ? source.AudioBitrateKbps
            : 192;
        AudioQuality = Math.Clamp(source.AudioQuality, 0, 10);
        FlacCompressionLevel = Math.Clamp(source.FlacCompressionLevel, 0, 12);
        SelectedPcmBitDepth = source.AudioCodec == RecordingAudioCodec.PcmS24Le
            ? PcmBitDepths[1]
            : PcmBitDepths[0];
        _suppressSelectionGuard = false;

        RefreshPanels();
        RefreshCompatibilityNote();
    }

    public ObservableCollection<AudioCodecChoice> AvailableCodecs { get; }

    public IReadOnlyList<int> BitrateOptionsList => BitrateOptions;

    public IReadOnlyList<NamedOption> PcmBitDepthOptions => PcmBitDepths;

    public string ContextSummary { get; }

    [ObservableProperty]
    private AudioCodecChoice _selectedCodec = null!;

    [ObservableProperty]
    private int _selectedBitrateKbps = 192;

    [ObservableProperty]
    private int _audioQuality = 5;

    [ObservableProperty]
    private int _flacCompressionLevel = 5;

    [ObservableProperty]
    private NamedOption _selectedPcmBitDepth = PcmBitDepths[0];

    [ObservableProperty]
    private string _compatibilityNote = "";

    [ObservableProperty]
    private bool _showBitrateOptions;

    [ObservableProperty]
    private bool _showFlacOptions;

    [ObservableProperty]
    private bool _showPcmOptions;

    [ObservableProperty]
    private bool _showVorbisOptions;

    public bool DialogAccepted { get; private set; }

    public event EventHandler? RequestClose;

    partial void OnSelectedCodecChanged(AudioCodecChoice value)
    {
        if (_suppressSelectionGuard || value is null) return;

        if (!value.IsEnabled)
        {
            // ComboBox may still raise selection for disabled items on some themes — snap back.
            _suppressSelectionGuard = true;
            SelectedCodec = AvailableCodecs.First(c => c.IsEnabled);
            _suppressSelectionGuard = false;
            CompatibilityNote =
                $"{value.DisplayName} cannot be muxed into {_source.Container.ToUpperInvariant()}.";
            RefreshPanels();
            return;
        }

        RefreshPanels();
        RefreshCompatibilityNote();
    }

    private void RefreshPanels()
    {
        var id = SelectedCodec?.Id ?? CodecAac;
        ShowBitrateOptions = id is CodecAac or CodecMp3;
        ShowFlacOptions = id == CodecFlac;
        ShowPcmOptions = id == CodecPcm;
        ShowVorbisOptions = id == CodecVorbis;
    }

    private void RefreshCompatibilityNote()
    {
        var enabled = AvailableCodecs.Where(c => c.IsEnabled).Select(c => c.DisplayName);
        CompatibilityNote = $"Available for {_source.Container.ToUpperInvariant()}: {string.Join(", ", enabled)}.";
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
        var audio = ResolveAudioCodec();
        return _source with
        {
            Id = $"custom-{_source.VideoCodec.ToString().ToLowerInvariant()}-{_source.Container}",
            AudioCodec = audio,
            AudioBitrateKbps = Math.Clamp(SelectedBitrateKbps, 64, 512),
            AudioQuality = Math.Clamp(AudioQuality, 0, 10),
            FlacCompressionLevel = Math.Clamp(FlacCompressionLevel, 0, 12),
        };
    }

    private RecordingAudioCodec ResolveAudioCodec()
    {
        return SelectedCodec.Id switch
        {
            CodecAac => RecordingAudioCodec.Aac,
            CodecFlac => RecordingAudioCodec.Flac,
            CodecMp3 => RecordingAudioCodec.Mp3,
            CodecVorbis => RecordingAudioCodec.Vorbis,
            CodecPcm => SelectedPcmBitDepth.Id == "24"
                ? RecordingAudioCodec.PcmS24Le
                : RecordingAudioCodec.PcmS16Le,
            _ => RecordingProfile.DefaultAudioCodec(_source.VideoCodec, _source.Container),
        };
    }

    private AudioCodecChoice ResolveInitialCodec(RecordingProfile source)
    {
        var id = source.AudioCodec switch
        {
            RecordingAudioCodec.Aac => CodecAac,
            RecordingAudioCodec.Flac => CodecFlac,
            RecordingAudioCodec.Mp3 => CodecMp3,
            RecordingAudioCodec.Vorbis => CodecVorbis,
            RecordingAudioCodec.PcmS16Le or RecordingAudioCodec.PcmS24Le => CodecPcm,
            _ => CodecAac,
        };

        var choice = AvailableCodecs.FirstOrDefault(c => c.Id == id && c.IsEnabled)
            ?? AvailableCodecs.First(c => c.IsEnabled);
        return choice;
    }

    private static string FormatVideo(RecordingCodec codec) => codec switch
    {
        RecordingCodec.H264 => "H.264",
        RecordingCodec.H265 => "H.265",
        RecordingCodec.Ffv1 => "FFV1",
        RecordingCodec.ProRes => "ProRes",
        _ => codec.ToString(),
    };
}
