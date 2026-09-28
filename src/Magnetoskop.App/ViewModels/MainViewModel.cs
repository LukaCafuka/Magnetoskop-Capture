using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magnetoskop.App.Services;
using Magnetoskop.App.Views;
using Magnetoskop.Capture.Audio;
using Magnetoskop.Capture.Video;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.App.ViewModels;

public sealed record VideoStandardOption(VideoInputStandard Value, string DisplayName);
public sealed record VideoScanModeOption(VideoScanMode Value, string DisplayName);

/// <summary>
/// Main window view model: transport control, timecode/status display,
/// device selection, preview rendering, and recording control.
/// All hardware access goes through the Core abstractions.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly VtrConnectionService _vtr;
    private readonly IVideoCaptureService _videoCapture;
    private readonly IAudioCaptureService _audioCapture;
    private readonly AudioMonitorService _audioMonitor;
    private readonly IRecordingService _recorder;
    private readonly CaptureSessionCoordinator _session;
    private readonly SettingsService _settings;
    private readonly DebugSessionFileLoggerProvider _debugLogger;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _freshnessTimer;

    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private CaptureSubscription<VideoFrame>? _previewSubscription;
    private WriteableBitmap? _previewBitmap;
    private readonly PreviewBobDeinterlacer _previewBob = new();
    private bool _suppressPreviewRestart;
    private bool _suppressVtrConnect;
    /// <summary>False while the ctor applies persisted settings so property changers do not overwrite the file early.</summary>
    private bool _settingsReady;
    private DateTimeOffset _lastWheelSend = DateTimeOffset.MinValue;
    private byte _lastWheelSpeed = 255;
    private bool _lastWheelForward = true;
    private VariableSpeedMode _lastWheelMode;
    private bool _suppressWheelSend;
    /// <summary>True while a variable-speed command is awaiting the deck.</summary>
    private bool _wheelSendInFlight;
    /// <summary>Set when a newer wheel position arrives during an in-flight send — flush after.</summary>
    private bool _wheelSendPending;
    private int _wheelReleaseEpoch;
    private WatchWindow? _watchWindow;
    private ConnectionsWindow? _connectionsWindow;
    private TransportState _currentTransport = TransportState.Unknown;
    private TimeInformation _latestTimeInformation = new();
    private VtrLinkHealth _latestVtrLinkHealth = new();
    private CaptureHealth _latestVideoHealth = new();
    private long _previewStartedTimestamp100ns;
    private long _lastPreviewFrameTimestamp100ns;
    private bool _suppressVideoConfigurationPersistence;
    private bool _suppressVideoFormatAcknowledgmentPersistence;
    private bool _suppressCalibrationPersistence;
    private bool _previewTransitionInProgress;
    private Task _previewTransitionTask = Task.CompletedTask;
    private bool _recordingStartInProgress;
    private TaskCompletionSource<bool>? _recordingStartCompletion;
    /// <summary>JKL shuttle step: 0 stopped, +n forward, −n reverse.</summary>
    private int _jklStep;
    /// <summary>Linear peak hold (0…1) before dBFS mapping for the UI meters.</summary>
    private double _linearMeterLeft;
    private double _linearMeterRight;

    public MainViewModel(
        VtrConnectionService vtr,
        IVideoCaptureService videoCapture,
        IAudioCaptureService audioCapture,
        AudioMonitorService audioMonitor,
        IRecordingService recorder,
        CaptureSessionCoordinator session,
        SettingsService settings,
        DebugSessionFileLoggerProvider debugLogger,
        ILogger<MainViewModel> logger)
    {
        _vtr = vtr;
        _videoCapture = videoCapture;
        _audioCapture = audioCapture;
        _audioMonitor = audioMonitor;
        _recorder = recorder;
        _session = session;
        _settings = settings;
        _debugLogger = debugLogger;
        _logger = logger;

        _vtr.StatusChanged += OnVtrStatusChanged;
        _vtr.TimeChanged += OnVtrTimeChanged;
        _vtr.LinkHealthChanged += OnVtrLinkHealthChanged;
        _vtr.CapabilityLearned += OnCapabilityLearned;
        _recorder.StatusChanged += OnRecordingStatusChanged;
        _videoCapture.HealthChanged += OnVideoCaptureHealthChanged;

        var saved = _settings.Load();
        if (!string.IsNullOrEmpty(saved.FfmpegPath))
        {
            Recording.FfmpegLocator.ConfiguredPath = saved.FfmpegPath;
        }

        // No default folder: Record stays disabled until the user picks a path (File menu).
        OutputDirectory = saved.OutputDirectory is { Length: > 0 } dir && Directory.Exists(dir)
            ? dir
            : "";
        SelectedRecordingProfile = (saved.Video ?? VideoEncodeSettings.FromProfile(RecordingProfile.CreateDefault()))
            .ToProfile();

        VtrConnections = new ObservableCollection<VtrConnectionOption>(_vtr.GetConnectionOptions());
        VtrProfiles = new ObservableCollection<VtrDeviceProfile>(_vtr.GetDeviceProfiles());
        _suppressVtrConnect = true;
        SelectedVtrConnection = VtrConnections.FirstOrDefault(o => o.Id == saved.VtrConnectionId)
            ?? VtrConnections.FirstOrDefault();
        _suppressVtrConnect = false;
        SelectedVtrProfile = VtrProfiles.FirstOrDefault(p => p.Id == saved.VtrProfileId)
            ?? VtrProfiles.FirstOrDefault(p => p.Id == VtrDeviceProfile.Generic.Id)
            ?? VtrDeviceProfile.Generic;
        AudioManuallySelected = saved.AudioManuallySelected;
        AudioMonitoringEnabled = saved.AudioMonitoringEnabled;
        MonitorVolumePercent = Math.Clamp(saved.MonitorVolumePercent is >= 0 and <= 200
            ? saved.MonitorVolumePercent
            : 100, 0, 200);
        _audioMonitor.Volume = MonitorVolumePercent / 100f;
        AutoPlayOnRecord = saved.AutoPlayOnRecord;
        ShowLogPanel = saved.ShowLogPanel;
        DisableTransportDuringRecording = saved.DisableTransportDuringRecording;
        MediaKeysControlTransport = saved.MediaKeysControlTransport;
        PreviewYadif2xEnabled = saved.PreviewYadif2xEnabled;
        Ctl24HourWrap = saved.Ctl24HourWrap;
        // Keep runtime logger in sync with persisted preference (also set at host bootstrap).
        _debugLogger.SetEnabled(saved.DebugLoggingEnabled);

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _meterTimer.Tick += (_, _) => UpdateAudioMeters();
        _meterTimer.Start();

        _freshnessTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _freshnessTimer.Tick += (_, _) => RefreshFreshnessIndicators();
        _freshnessTimer.Start();

        // Property changers (e.g. AudioMonitoringEnabled) may SaveSettings; allow that only after load.
        _settingsReady = true;
        RefreshStatusBarCodec();
    }

    // ---- Observable state ------------------------------------------------

    [ObservableProperty]
    private string _deviceDescription = "(not connected)";

    [ObservableProperty]
    private bool _isVtrConnected;

    /// <summary>True when status-sense reports Standby (threaded stop). Drives the Standby toggle color.</summary>
    [ObservableProperty]
    private bool _isStandbyOn;

    [ObservableProperty]
    private string _transportStateText = "—";

    [ObservableProperty]
    private string _ctlText = "--:--:--:--";

    [ObservableProperty]
    private string _ctlFreshnessText = "N/A";

    [ObservableProperty]
    private bool _isEditingCtl;

    [ObservableProperty]
    private string _ctlEditText = "00:00:00:00";

    [ObservableProperty]
    private string _ltcText = "--:--:--:--";

    [ObservableProperty]
    private string _ltcFreshnessText = "N/A";

    [ObservableProperty]
    private bool _isEditingLtc;

    [ObservableProperty]
    private string _ltcEditText = "00:00:00:00";

    [ObservableProperty]
    private string _vitcText = "--:--:--:--";

    [ObservableProperty]
    private string _vitcFreshnessText = "N/A";

    [ObservableProperty]
    private bool _isEditingVitc;

    [ObservableProperty]
    private string _vitcEditText = "00:00:00:00";

    [ObservableProperty]
    private string _userBitsText = "-- -- -- --";

    [ObservableProperty]
    private string _userBitsFreshnessText = "N/A";

    [ObservableProperty]
    private string _vtrLinkHealthText = "VTR disconnected";

    [ObservableProperty]
    private bool _isVtrLinkStaleOrLost;

    [ObservableProperty]
    private bool _isVtrLinkLost = true;

    [ObservableProperty]
    private string _statusFlagsText = "";

    /// <summary>Device-type identification result shown in the recorder status panel.</summary>
    [ObservableProperty]
    private string _detectedProfileText = "";

    /// <summary>When true, the bottom LOG panel is visible.</summary>
    [ObservableProperty]
    private bool _showLogPanel;

    /// <summary>When true, VTR transport buttons are disabled while recording.</summary>
    [ObservableProperty]
    private bool _disableTransportDuringRecording = true;

    /// <summary>When true, keyboard media keys drive VTR transport while the app has focus.</summary>
    [ObservableProperty]
    private bool _mediaKeysControlTransport = true;

    /// <summary>When true, live preview applies Yadif 2× (bob) deinterlace for watching.</summary>
    [ObservableProperty]
    private bool _previewYadif2xEnabled;

    /// <summary>When true, CTL below zero uses 24h wrap; when false (default), signed with '-'.</summary>
    [ObservableProperty]
    private bool _ctl24HourWrap;

    /// <summary>Jog vs Shuttle for the variable-speed wheel.</summary>
    [ObservableProperty]
    private VariableSpeedMode _jogShuttleMode = VariableSpeedMode.Shuttle;

    /// <summary>Wheel deflection in [-1, +1]; 0 = still.</summary>
    [ObservableProperty]
    private double _wheelPosition;

    [ObservableProperty]
    private string _wheelSpeedLabel = "0.00×";

    /// <summary>Bound to enable/disable the wheel while recording lock is on.</summary>
    public bool CanUseTransportButtons => CanUseTransport();

    public bool IsJogMode
    {
        get => JogShuttleMode == VariableSpeedMode.Jog;
        set
        {
            if (value) JogShuttleMode = VariableSpeedMode.Jog;
        }
    }

    public bool IsShuttleMode
    {
        get => JogShuttleMode == VariableSpeedMode.Shuttle;
        set
        {
            if (value) JogShuttleMode = VariableSpeedMode.Shuttle;
        }
    }

    /// <summary>Command support learned from the deck's NAK responses.</summary>
    [ObservableProperty]
    private string _capabilitiesText = "";

    [ObservableProperty]
    private ImageSource? _previewSource;

    [ObservableProperty]
    private bool _isPreviewRunning;

    [ObservableProperty]
    private string _previewHealthText = "";

    [ObservableProperty]
    private bool _isPreviewHealthVisible;

    [ObservableProperty]
    private bool _isPreviewLost;

    [ObservableProperty]
    private CaptureDeviceInfo? _selectedVideoDevice;

    [ObservableProperty]
    private CaptureDeviceInfo? _selectedAudioDevice;

    [ObservableProperty]
    private VideoInputStandard _selectedVideoInputStandard;

    [ObservableProperty]
    private VideoScanMode _selectedVideoScanMode;

    [ObservableProperty]
    private int _customVideoWidth = 720;

    [ObservableProperty]
    private int _customVideoHeight = 576;

    [ObservableProperty]
    private double _customVideoFrameRate = 25;

    [ObservableProperty]
    private bool _isVideoFormatMismatchAcknowledged;

    [ObservableProperty]
    private string _videoInputFormatStatusText = "Open the preview to verify the driver format.";

    [ObservableProperty]
    private double _audioCalibrationOffsetMilliseconds;

    [ObservableProperty]
    private bool _isAvCalibrationCalibrated;

    [ObservableProperty]
    private DateTimeOffset? _avCalibrationMeasuredAt;

    [ObservableProperty]
    private string _avCalibrationStatusText = "Uncalibrated — 0.000 ms default";

    /// <summary>When true the user picked an audio device manually; otherwise auto-select applies.</summary>
    [ObservableProperty]
    private bool _audioManuallySelected;

    /// <summary>When true, play the live capture input through the default output device.</summary>
    [ObservableProperty]
    private bool _audioMonitoringEnabled;

    /// <summary>Monitor playback volume 0–200%. Does not affect recording or level meters.</summary>
    [ObservableProperty]
    private int _monitorVolumePercent = 100;

    /// <summary>Formatted monitor volume for the slider readout.</summary>
    public string MonitorVolumeLabel => $"{MonitorVolumePercent}%";

    [ObservableProperty]
    private string _outputDirectory = "";

    /// <summary>True when a valid, existing output folder has been chosen.</summary>
    public bool HasOutputDirectory
        => !string.IsNullOrWhiteSpace(OutputDirectory) && Directory.Exists(OutputDirectory);

    /// <summary>Path for the RECORDING panel, or a hint when unset.</summary>
    public string OutputDirectoryDisplay
        => HasOutputDirectory
            ? OutputDirectory
            : "Not set — use File → Choose save location…";

    [ObservableProperty]
    private RecordingProfile? _selectedRecordingProfile;

    /// <summary>Summary of the current video encode settings for the RECORDING panel.</summary>
    public string VideoSettingsSummary
        => SelectedRecordingProfile?.DisplayName ?? "Not configured — use Video → Video settings…";

    [ObservableProperty]
    private bool _isRecording;

    /// <summary>When true, Record issues Play on the deck and waits for servo lock first.</summary>
    [ObservableProperty]
    private bool _autoPlayOnRecord;

    [ObservableProperty]
    private string _recordingStatusText = "Idle";

    [ObservableProperty]
    private string _recordingSyncStatusText = "";

    [ObservableProperty]
    private bool _isRecordingSyncStatusVisible;

    [ObservableProperty]
    private Brush _recordingSyncStatusBrush = Brushes.Gray;

    /// <summary>Bottom status bar: Idle / ● REC / Starting…</summary>
    [ObservableProperty]
    private string _statusBarRecordingText = "Idle";

    /// <summary>Bottom status bar: recording elapsed, or em dash when idle.</summary>
    [ObservableProperty]
    private string _statusBarElapsedText = "—";

    /// <summary>Bottom status bar: short codec/container summary.</summary>
    [ObservableProperty]
    private string _statusBarCodecText = "—";

    /// <summary>Bottom status bar: VTR transport or offline.</summary>
    [ObservableProperty]
    private string _statusBarTransportText = "VTR offline";

    [ObservableProperty]
    private string _lastError = "";

    [ObservableProperty]
    private VtrConnectionOption? _selectedVtrConnection;

    [ObservableProperty]
    private VtrDeviceProfile? _selectedVtrProfile;

    [ObservableProperty]
    private double _audioLevelLeft;

    [ObservableProperty]
    private double _audioLevelRight;

    public ObservableCollection<VtrConnectionOption> VtrConnections { get; }
    public ObservableCollection<VtrDeviceProfile> VtrProfiles { get; }
    public ObservableCollection<CaptureDeviceInfo> VideoDevices { get; } = new();
    public ObservableCollection<CaptureDeviceInfo> AudioDevices { get; } = new();
    public ObservableCollection<string> LogEntries { get; } = new();

    public IReadOnlyList<VideoStandardOption> VideoStandardOptions { get; } = new[]
    {
        new VideoStandardOption(VideoInputStandard.Unspecified, "Select standard…"),
        new VideoStandardOption(VideoInputStandard.Pal, "PAL — 720×576 @ 25"),
        new VideoStandardOption(VideoInputStandard.Ntsc, "NTSC — 720×480 @ 29.97"),
        new VideoStandardOption(VideoInputStandard.Custom, "Custom"),
    };

    public IReadOnlyList<VideoScanModeOption> VideoScanModeOptions { get; } = new[]
    {
        new VideoScanModeOption(VideoScanMode.Unspecified, "Select scan mode…"),
        new VideoScanModeOption(VideoScanMode.Progressive, "Progressive"),
        new VideoScanModeOption(VideoScanMode.Tff, "Interlaced — top field first (TFF)"),
        new VideoScanModeOption(VideoScanMode.Bff, "Interlaced — bottom field first (BFF)"),
    };

    public VideoInputConfiguration CurrentVideoInputConfiguration => new()
    {
        Standard = SelectedVideoInputStandard,
        ScanMode = SelectedVideoScanMode,
        CustomWidth = CustomVideoWidth,
        CustomHeight = CustomVideoHeight,
        CustomFrameRate = CustomVideoFrameRate,
    };

    public bool IsCustomVideoInputStandard => SelectedVideoInputStandard == VideoInputStandard.Custom;

    public bool HasExplicitVideoInputConfiguration
        => SelectedVideoDevice?.Id.StartsWith("sim:", StringComparison.Ordinal) == true
           || CurrentVideoInputConfiguration.IsExplicit;

    public string VideoInputConfigurationStatusText
        => HasExplicitVideoInputConfiguration
            ? $"{SelectedVideoInputStandard} · {SelectedVideoScanMode}"
            : "Required before recording: select the input standard and scan mode.";

    public VideoInputFormatStatus? CurrentVideoInputFormatStatus
        => (_videoCapture as IConfigurableVideoCaptureService)?.FormatStatus;

    /// <summary>True only for a concrete driver readback that differs from the request.</summary>
    public bool IsVideoFormatAcknowledgmentRequired
        => CurrentVideoInputFormatStatus is { Actual: not null, AcknowledgmentRequired: true };

    /// <summary>
    /// Recording may proceed after a matching readback, or after the operator has
    /// acknowledged this exact requested/actual pair. Pending readback remains blocked.
    /// </summary>
    public bool CanRecordWithVideoInputFormat
    {
        get
        {
            if (!HasExplicitVideoInputConfiguration) return false;
            var status = CurrentVideoInputFormatStatus;
            if (status is null) return true; // Capture services without the optional capability.
            if (!status.AcknowledgmentRequired) return true;
            return status.Actual is not null && IsVideoFormatMismatchAcknowledged;
        }
    }

    public string? VideoFormatAcknowledgmentKey
        => SelectedVideoDevice is { } device
           && CurrentVideoInputFormatStatus is { Actual: not null } status
            ? BuildVideoFormatAcknowledgmentKey(device.Id, status)
            : null;

    /// <summary>Only a measured/confirmed offset is applied; uncalibrated always means zero.</summary>
    public long EffectiveAudioOffset100ns
        => IsAvCalibrationCalibrated
            ? checked((long)Math.Round(AudioCalibrationOffsetMilliseconds * TimeSpan.TicksPerMillisecond))
            : 0;

    public string? CurrentAvCalibrationKey
        => SelectedVideoDevice is { } video && SelectedAudioDevice is { } audio
            ? $"v1|video:{video.Id.Length}:{video.Id}|audio:{audio.Id.Length}:{audio.Id}"
            : null;

    // ---- Lifecycle ---------------------------------------------------------

    [RelayCommand]
    private async Task InitializeAsync()
    {
        try
        {
            await RefreshDevicesCoreAsync();

            // Reconnect to the saved VTR target (COM port + profile) when one was
            // persisted; otherwise connect the default simulator.
            if (SelectedVtrConnection is { } connection
                && connection.Id != VtrConnectionOption.SimulatorId
                && SelectedVtrProfile is { } profile)
            {
                try
                {
                    await _vtr.SwitchAsync(connection, profile);
                }
                catch (Exception ex)
                {
                    ReportError($"Failed to reconnect to {connection.DisplayName}; falling back to the simulator", ex);
                    SelectedVtrConnection = VtrConnections.FirstOrDefault(o => o.Id == VtrConnectionOption.SimulatorId);
                    await _vtr.SwitchAsync(SelectedVtrConnection!, SelectedVtrProfile ?? VtrDeviceProfile.Generic);
                }
            }
            else
            {
                await _vtr.ConnectAsync();
            }

            IsVtrConnected = _vtr.IsConnected;
            DeviceDescription = _vtr.DeviceDescription;
            AppendLog($"Connected: {_vtr.DeviceDescription}");
            UpdateCompatibilityInfo();

            await RunPreviewTransitionAsync(StartPreviewAsync);
        }
        catch (Exception ex)
        {
            ReportError("Initialization failed", ex);
        }
    }

    partial void OnSelectedVtrConnectionChanged(VtrConnectionOption? value)
    {
        if (_suppressVtrConnect || value is null) return;
        _ = ApplyVtrConnectionAsync();
    }

    partial void OnSelectedVtrProfileChanged(VtrDeviceProfile? value)
    {
        UpdateWheelSpeedLabel();

        if (!_settingsReady || value is null) return;
        SaveSettings();

        // Reconnect a live COM session so the new profile takes effect immediately.
        if (SelectedVtrConnection is { } connection
            && connection.Id != VtrConnectionOption.SimulatorId
            && _vtr.IsConnected)
        {
            _ = ApplyVtrConnectionAsync();
        }
    }

    /// <summary>Deck-specific shuttle ceiling (DVW-M2000P = 42×; others default 50×).</summary>
    private double CurrentMaxShuttleRate
        => SelectedVtrProfile?.MaxShuttleRate
           ?? VariableSpeedEncoding.DefaultMaxShuttleRate;

    private async Task ApplyVtrConnectionAsync()
    {
        if (SelectedVtrConnection is null || SelectedVtrProfile is null) return;
        try
        {
            await _vtr.SwitchAsync(SelectedVtrConnection, SelectedVtrProfile);
            IsVtrConnected = _vtr.IsConnected;
            DeviceDescription = _vtr.DeviceDescription;
            AppendLog($"Connected: {_vtr.DeviceDescription}");
            UpdateCompatibilityInfo();
        }
        catch (Exception ex)
        {
            IsVtrConnected = false;
            StatusBarTransportText = "VTR offline";
            ReportError($"Failed to connect to {SelectedVtrConnection.DisplayName}", ex);
        }
    }

    /// <summary>Refreshes the detected-profile and learned-capabilities display.</summary>
    private void UpdateCompatibilityInfo()
    {
        var detected = _vtr.DetectedProfile;
        if (detected is null)
        {
            DetectedProfileText = "";
        }
        else if (detected.Id == VtrDeviceProfile.Generic.Id)
        {
            DetectedProfileText = "Device type not recognized — using selected profile";
        }
        else
        {
            DetectedProfileText = $"Identified: {detected.DisplayName}";
            if (SelectedVtrProfile is not null && SelectedVtrProfile.Id != detected.Id)
            {
                AppendLog($"NOTE  Deck identified as {detected.DisplayName} but profile " +
                          $"'{SelectedVtrProfile.DisplayName}' is selected.");
            }
        }

        var learned = _vtr.LearnedCapabilities;
        CapabilitiesText = learned is null or { Count: 0 }
            ? ""
            : "Learned: " + string.Join("  ", learned
                .OrderBy(kv => kv.Key.ToString())
                .Select(kv => $"{kv.Key} {(kv.Value ? "✓" : "✗")}"));
    }

    private void OnCapabilityLearned(object? sender, TransportCommand command)
    {
        RunOnUi(UpdateCompatibilityInfo);
    }

    [RelayCommand]
    private void RefreshVtrConnections()
    {
        var previousId = SelectedVtrConnection?.Id;
        _suppressVtrConnect = true;
        try
        {
            VtrConnections.Clear();
            foreach (var option in _vtr.GetConnectionOptions()) VtrConnections.Add(option);
            SelectedVtrConnection = VtrConnections.FirstOrDefault(o => o.Id == previousId)
                ?? VtrConnections.FirstOrDefault();
        }
        finally
        {
            _suppressVtrConnect = false;
        }

        // Only reconnect when the restored selection differs (e.g. previous COM port gone).
        if (SelectedVtrConnection is not null
            && SelectedVtrConnection.Id != previousId)
        {
            _ = ApplyVtrConnectionAsync();
        }
    }

    public async Task ShutdownAsync()
    {
        _meterTimer.Stop();
        _freshnessTimer.Stop();
        SaveSettings();
        var recordingStart = _recordingStartCompletion;
        if (recordingStart is not null)
        {
            // A close can arrive while auto-play/preflight is awaiting. Let that
            // start attempt settle before asking the coordinator to stop/finalize.
            await recordingStart.Task;
        }
        try
        {
            // Also await an automatic incomplete/faulted session whose completion
            // monitor may still be flushing its audit and atomic final summary.
            await _session.StopForShutdownAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording artifacts could not be finalized during shutdown");
        }

        try
        {
            // Device/config changes restart preview asynchronously. Do not race a
            // close-time capture stop against an in-flight reopen.
            await _previewTransitionTask;
            await StopPreviewInternalAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture devices could not be stopped cleanly during shutdown");
        }

        try
        {
            await _vtr.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VTR could not be disconnected cleanly during shutdown");
        }
    }

    private void SaveSettings()
    {
        if (!_settingsReady) return;

        var s = _settings.Current;
        s.OutputDirectory = OutputDirectory;
        if (SelectedRecordingProfile is not null)
        {
            s.Video = VideoEncodeSettings.FromProfile(SelectedRecordingProfile);
        }
        s.VideoDeviceId = SelectedVideoDevice?.Id;
        s.VideoDeviceName = SelectedVideoDevice?.Name;
        s.AudioDeviceId = SelectedAudioDevice?.Id;
        s.AudioManuallySelected = AudioManuallySelected;
        s.AudioMonitoringEnabled = AudioMonitoringEnabled;
        s.MonitorVolumePercent = Math.Clamp(MonitorVolumePercent, 0, 200);
        s.VtrConnectionId = SelectedVtrConnection?.Id;
        s.VtrProfileId = SelectedVtrProfile?.Id;
        s.FfmpegPath = Recording.FfmpegLocator.ConfiguredPath;
        s.AutoPlayOnRecord = AutoPlayOnRecord;
        s.DebugLoggingEnabled = _debugLogger.Enabled;
        s.ShowLogPanel = ShowLogPanel;
        s.DisableTransportDuringRecording = DisableTransportDuringRecording;
        s.MediaKeysControlTransport = MediaKeysControlTransport;
        s.PreviewYadif2xEnabled = PreviewYadif2xEnabled;
        s.Ctl24HourWrap = Ctl24HourWrap;
        _settings.Save();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var vm = new SettingsViewModel(
            _debugLogger.Enabled,
            ShowLogPanel,
            DisableTransportDuringRecording,
            MediaKeysControlTransport,
            PreviewYadif2xEnabled,
            Ctl24HourWrap);
        var window = new SettingsWindow(vm)
        {
            Owner = Application.Current?.MainWindow,
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        ShowLogPanel = vm.ShowLogPanel;
        DisableTransportDuringRecording = vm.DisableTransportDuringRecording;
        MediaKeysControlTransport = vm.MediaKeysControlTransport;
        PreviewYadif2xEnabled = vm.PreviewYadif2xEnabled;
        Ctl24HourWrap = vm.Ctl24HourWrap;
        _debugLogger.SetEnabled(vm.DebugLoggingEnabled);
        SaveSettings();

        AppendLog(vm.DebugLoggingEnabled
            ? $"Debug logging enabled → {_debugLogger.CurrentLogPath ?? DebugSessionFileLoggerProvider.LogDirectory}"
            : "Debug logging disabled");
    }

    [RelayCommand(CanExecute = nameof(CanEditRecordingSettings))]
    private void OpenVideoSettings()
    {
        var source = SelectedRecordingProfile ?? RecordingProfile.CreateDefault();
        var vm = new VideoSettingsViewModel(source);
        var window = new VideoSettingsWindow(vm)
        {
            Owner = Application.Current?.MainWindow,
        };
        if (window.ShowDialog() == true)
        {
            SelectedRecordingProfile = vm.ToProfile();
            SaveSettings();
            AppendLog($"Video settings: {SelectedRecordingProfile.DisplayName}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditRecordingSettings))]
    private void OpenAudioSettings()
    {
        var source = SelectedRecordingProfile ?? RecordingProfile.CreateDefault();
        var vm = new AudioSettingsViewModel(source);
        var window = new AudioSettingsWindow(vm)
        {
            Owner = Application.Current?.MainWindow,
        };
        if (window.ShowDialog() == true)
        {
            SelectedRecordingProfile = vm.ToProfile();
            SaveSettings();
            AppendLog($"Audio settings: {SelectedRecordingProfile.AudioCodecDisplayName}");
        }
    }

    [RelayCommand]
    private void OpenWatchWindow()
    {
        if (_watchWindow is { IsLoaded: true })
        {
            if (_watchWindow.WindowState == WindowState.Minimized)
            {
                _watchWindow.WindowState = WindowState.Maximized;
            }

            _watchWindow.Activate();
            return;
        }

        var window = new WatchWindow(this)
        {
            Owner = Application.Current?.MainWindow,
        };
        _watchWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_watchWindow, window))
            {
                _watchWindow = null;
            }
        };
        window.Show();
    }

    [RelayCommand]
    private void CloseWatchWindow()
    {
        _watchWindow?.Close();
    }

    [RelayCommand]
    private void OpenConnectionsWindow()
    {
        if (_connectionsWindow is { IsLoaded: true })
        {
            if (_connectionsWindow.WindowState == WindowState.Minimized)
            {
                _connectionsWindow.WindowState = WindowState.Normal;
            }

            _connectionsWindow.Activate();
            return;
        }

        var window = new ConnectionsWindow(this)
        {
            Owner = Application.Current?.MainWindow,
        };
        _connectionsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_connectionsWindow, window))
            {
                _connectionsWindow = null;
            }
        };
        window.Show();
    }

    [RelayCommand]
    private void CloseConnectionsWindow()
    {
        _connectionsWindow?.Close();
    }

    // ---- Device selection ----------------------------------------------------

    [RelayCommand]
    private Task RefreshDevicesAsync()
        => RunPreviewTransitionAsync(RefreshDevicesAndPreviewAsync);

    private async Task RefreshDevicesAndPreviewAsync()
    {
        await RefreshDevicesCoreAsync();
        if (!IsRecording)
        {
            await RestartPreviewAsync();
        }
    }

    private async Task RefreshDevicesCoreAsync()
    {
        _suppressPreviewRestart = true;
        try
        {
            var video = await _videoCapture.EnumerateDevicesAsync();
            var audio = await _audioCapture.EnumerateDevicesAsync();

            VideoDevices.Clear();
            foreach (var d in video) VideoDevices.Add(d);
            AudioDevices.Clear();
            foreach (var d in audio) AudioDevices.Add(d);

            var saved = _settings.Current;
            SelectedVideoDevice = ResolveVideoDevice(saved.VideoDeviceId, saved.VideoDeviceName);
            await RestoreAudioDeviceAsync(saved.AudioDeviceId);
            LoadVideoInputConfiguration();
            LoadAvCalibration();
        }
        catch (Exception ex)
        {
            ReportError("Device enumeration failed", ex);
        }
        finally
        {
            _suppressPreviewRestart = false;
        }

        if (_settingsReady)
        {
            SaveSettings();
        }
    }

    /// <summary>
    /// Restore the stable DirectShow identity first. Friendly-name matching is retained
    /// only for settings written by the older numeric/index-based implementation.
    /// </summary>
    private CaptureDeviceInfo? ResolveVideoDevice(string? savedId, string? savedName)
        => ResolveVideoDevice(VideoDevices, savedId, savedName);

    internal static CaptureDeviceInfo? ResolveVideoDevice(
        IReadOnlyList<CaptureDeviceInfo> devices,
        string? savedId,
        string? savedName)
    {
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            var byId = devices.FirstOrDefault(d =>
                string.Equals(d.Id, savedId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
        }

        if (IsLegacyVideoDeviceId(savedId) && !string.IsNullOrWhiteSpace(savedName))
        {
            var byName = devices.FirstOrDefault(d =>
                string.Equals(d.Name, savedName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }

        return devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
    }

    private static bool IsLegacyVideoDeviceId(string? deviceId)
        => string.IsNullOrWhiteSpace(deviceId)
           || int.TryParse(deviceId, NumberStyles.None, CultureInfo.InvariantCulture, out _)
           || deviceId.StartsWith("legacy-index:", StringComparison.OrdinalIgnoreCase);

    private async Task RestoreAudioDeviceAsync(string? savedId)
    {
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            var byId = AudioDevices.FirstOrDefault(d => d.Id == savedId);
            if (byId is not null)
            {
                SelectedAudioDevice = byId;
                return;
            }

            // Saved endpoint gone — drop manual lock and fall through.
            if (AudioManuallySelected)
            {
                AudioManuallySelected = false;
            }
        }

        if (!AudioManuallySelected)
        {
            await AutoSelectAudioAsync();
        }

        SelectedAudioDevice ??= AudioDevices.FirstOrDefault(d => d.IsDefault)
            ?? AudioDevices.FirstOrDefault();
    }

    partial void OnSelectedVideoDeviceChanged(CaptureDeviceInfo? value)
    {
        LoadVideoInputConfiguration();
        LoadAvCalibration();

        if (_settingsReady && !_suppressPreviewRestart)
        {
            SaveSettings();
        }

        if (_suppressPreviewRestart || IsRecording) return;

        if (value is not null && !AudioManuallySelected)
        {
            _ = RunPreviewTransitionAsync(ApplyVideoDeviceChangeAsync);
        }
        else
        {
            _ = RunPreviewTransitionAsync(RestartPreviewAsync);
        }
    }

    partial void OnSelectedAudioDeviceChanged(CaptureDeviceInfo? value)
    {
        LoadAvCalibration();

        if (_settingsReady && !_suppressPreviewRestart)
        {
            SaveSettings();
        }

        if (_suppressPreviewRestart || IsRecording) return;
        _ = RunPreviewTransitionAsync(RestartPreviewAsync);
    }

    partial void OnSelectedVideoInputStandardChanged(VideoInputStandard value)
        => ApplyVideoInputConfigurationChange();

    partial void OnSelectedVideoScanModeChanged(VideoScanMode value)
        => ApplyVideoInputConfigurationChange();

    partial void OnCustomVideoWidthChanged(int value)
        => ApplyVideoInputConfigurationChange();

    partial void OnCustomVideoHeightChanged(int value)
        => ApplyVideoInputConfigurationChange();

    partial void OnCustomVideoFrameRateChanged(double value)
        => ApplyVideoInputConfigurationChange();

    partial void OnIsVideoFormatMismatchAcknowledgedChanged(bool value)
    {
        if (_suppressVideoFormatAcknowledgmentPersistence) return;

        var deviceId = SelectedVideoDevice?.Id;
        var fingerprint = VideoFormatAcknowledgmentKey;
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(fingerprint))
        {
            if (value)
            {
                _suppressVideoFormatAcknowledgmentPersistence = true;
                IsVideoFormatMismatchAcknowledged = false;
                _suppressVideoFormatAcknowledgmentPersistence = false;
            }
            NotifyVideoInputFormatStatusChanged();
            return;
        }

        if (value)
        {
            _settings.Current.VideoFormatAcknowledgments[deviceId] =
                new VideoFormatAcknowledgmentSettings
                {
                    Fingerprint = fingerprint,
                    AcknowledgedAt = DateTimeOffset.UtcNow,
                };
        }
        else
        {
            _settings.Current.VideoFormatAcknowledgments.Remove(deviceId);
        }

        NotifyVideoInputFormatStatusChanged();
        SaveSettings();
    }

    partial void OnAudioCalibrationOffsetMillisecondsChanged(double value)
    {
        if (_suppressCalibrationPersistence) return;

        if (!double.IsFinite(value) || value is < -10_000 or > 10_000)
        {
            _suppressCalibrationPersistence = true;
            AudioCalibrationOffsetMilliseconds = Math.Clamp(
                double.IsFinite(value) ? value : 0,
                -10_000,
                10_000);
            _suppressCalibrationPersistence = false;
        }

        if (IsAvCalibrationCalibrated)
        {
            IsAvCalibrationCalibrated = false;
        }
        PersistAvCalibration();
    }

    partial void OnIsAvCalibrationCalibratedChanged(bool value)
    {
        if (_suppressCalibrationPersistence) return;
        AvCalibrationMeasuredAt = value ? DateTimeOffset.UtcNow : null;
        PersistAvCalibration();
    }

    private void LoadVideoInputConfiguration()
    {
        var configuration = SelectedVideoDevice?.Id.StartsWith("sim:", StringComparison.Ordinal) == true
            ? VideoInputConfiguration.SimulatedPalTff
            : SelectedVideoDevice is { } device
              && _settings.Current.VideoInputConfigurations.TryGetValue(device.Id, out var saved)
                ? saved
                : new VideoInputConfiguration();

        _suppressVideoConfigurationPersistence = true;
        SelectedVideoInputStandard = configuration.Standard;
        SelectedVideoScanMode = configuration.ScanMode;
        CustomVideoWidth = configuration.CustomWidth;
        CustomVideoHeight = configuration.CustomHeight;
        CustomVideoFrameRate = configuration.CustomFrameRate;
        _suppressVideoConfigurationPersistence = false;
        // OpenCV cannot safely renegotiate an active device. RestartPreviewAsync
        // stops it first and StartPreviewAsync applies the new request before open.
        if (!_videoCapture.IsCapturing)
        {
            ApplyConfigurationToCaptureService(configuration);
        }
        NotifyVideoInputConfigurationChanged();
    }

    private void ApplyVideoInputConfigurationChange()
    {
        OnPropertyChanged(nameof(IsCustomVideoInputStandard));
        if (_suppressVideoConfigurationPersistence) return;

        var configuration = CurrentVideoInputConfiguration;
        if (SelectedVideoDevice is { } device
            && !device.Id.StartsWith("sim:", StringComparison.Ordinal))
        {
            _settings.Current.VideoInputConfigurations[device.Id] = configuration;
        }

        if (!_videoCapture.IsCapturing)
        {
            ApplyConfigurationToCaptureService(configuration);
        }
        NotifyVideoInputConfigurationChanged();
        SaveSettings();

        if (!IsRecording && !_suppressPreviewRestart && SelectedVideoDevice is not null)
        {
            _ = RunPreviewTransitionAsync(RestartPreviewAsync);
        }
    }

    private void ApplyConfigurationToCaptureService(VideoInputConfiguration configuration)
    {
        if (_videoCapture is IConfigurableVideoCaptureService configurable)
        {
            configurable.InputConfiguration = configuration;
        }
    }

    private void NotifyVideoInputConfigurationChanged()
    {
        OnPropertyChanged(nameof(CurrentVideoInputConfiguration));
        OnPropertyChanged(nameof(HasExplicitVideoInputConfiguration));
        OnPropertyChanged(nameof(VideoInputConfigurationStatusText));
        RefreshVideoInputFormatStatus();
    }

    private void RefreshVideoInputFormatStatus()
    {
        var status = CurrentVideoInputFormatStatus;
        var fingerprint = VideoFormatAcknowledgmentKey;
        var acknowledged = SelectedVideoDevice is { } device
                           && fingerprint is not null
                           && _settings.Current.VideoFormatAcknowledgments.TryGetValue(device.Id, out var saved)
                           && string.Equals(saved.Fingerprint, fingerprint, StringComparison.Ordinal);

        _suppressVideoFormatAcknowledgmentPersistence = true;
        IsVideoFormatMismatchAcknowledged = acknowledged;
        _suppressVideoFormatAcknowledgmentPersistence = false;

        VideoInputFormatStatusText = DescribeVideoInputFormatStatus(status, acknowledged);
        NotifyVideoInputFormatStatusChanged();
    }

    private void NotifyVideoInputFormatStatusChanged()
    {
        OnPropertyChanged(nameof(CurrentVideoInputFormatStatus));
        OnPropertyChanged(nameof(IsVideoFormatAcknowledgmentRequired));
        OnPropertyChanged(nameof(CanRecordWithVideoInputFormat));
        OnPropertyChanged(nameof(VideoFormatAcknowledgmentKey));
        StartRecordingCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeVideoInputFormatStatus(
        VideoInputFormatStatus? status,
        bool acknowledged)
    {
        if (status is null)
        {
            return "This capture source does not expose driver-format readback.";
        }
        if (status.Actual is null)
        {
            return status.Message ?? "Open the preview to verify the driver format.";
        }

        var actual = status.Actual;
        var dimensions = FormattableString.Invariant(
            $"Driver: {actual.Width}×{actual.Height} @ {actual.FrameRate:0.###}");
        var scan = actual.Interlaced
            ? actual.TopFieldFirst ? "TFF" : "BFF"
            : "progressive";
        var readback = status.ScanReadbackAvailable
            ? $"{dimensions}, {scan}."
            : $"{dimensions}; scan order not reported.";
        if (!status.AcknowledgmentRequired)
        {
            return $"Verified — {readback}";
        }
        var reason = status.ScanReadbackAvailable
            ? "The driver readback differs from the request."
            : "The driver does not report scan order; verify it from the source/device settings.";
        return acknowledged
            ? $"Format limitation acknowledged for this exact readback — {readback}"
            : $"{reason} {readback} Acknowledge before recording.";
    }

    internal static string BuildVideoFormatAcknowledgmentKey(
        string deviceId,
        VideoInputFormatStatus status)
        => status.BuildAcknowledgmentKey(deviceId);

    private void LoadAvCalibration()
    {
        var saved = SelectedVideoDevice is { } video && SelectedAudioDevice is { } audio
            ? _settings.Current.AvCalibrations.FirstOrDefault(item =>
                string.Equals(item.VideoDeviceId, video.Id, StringComparison.Ordinal)
                && string.Equals(item.AudioDeviceId, audio.Id, StringComparison.Ordinal))
            : null;

        _suppressCalibrationPersistence = true;
        AudioCalibrationOffsetMilliseconds = saved?.AudioOffset100ns / (double)TimeSpan.TicksPerMillisecond ?? 0;
        IsAvCalibrationCalibrated = saved?.IsCalibrated == true;
        AvCalibrationMeasuredAt = saved?.MeasuredAt;
        _suppressCalibrationPersistence = false;
        RefreshAvCalibrationStatus();
    }

    private void PersistAvCalibration()
    {
        if (_suppressCalibrationPersistence
            || SelectedVideoDevice is not { } video
            || SelectedAudioDevice is not { } audio)
        {
            RefreshAvCalibrationStatus();
            return;
        }

        var items = _settings.Current.AvCalibrations;
        var existing = items.FirstOrDefault(item =>
            string.Equals(item.VideoDeviceId, video.Id, StringComparison.Ordinal)
            && string.Equals(item.AudioDeviceId, audio.Id, StringComparison.Ordinal));
        if (existing is null)
        {
            existing = new AvCalibrationSettings
            {
                VideoDeviceId = video.Id,
                AudioDeviceId = audio.Id,
            };
            items.Add(existing);
        }

        existing.AudioOffset100ns = checked((long)Math.Round(
            AudioCalibrationOffsetMilliseconds * TimeSpan.TicksPerMillisecond));
        existing.IsCalibrated = IsAvCalibrationCalibrated;
        existing.MeasuredAt = AvCalibrationMeasuredAt;
        RefreshAvCalibrationStatus();
        OnPropertyChanged(nameof(EffectiveAudioOffset100ns));
        SaveSettings();
    }

    private void RefreshAvCalibrationStatus()
    {
        AvCalibrationStatusText = IsAvCalibrationCalibrated
            ? $"Calibrated: {AudioCalibrationOffsetMilliseconds:+0.000;-0.000;0.000} ms" +
              (AvCalibrationMeasuredAt is { } at ? $" · {at.LocalDateTime:g}" : "")
            : $"Uncalibrated — {AudioCalibrationOffsetMilliseconds:+0.000;-0.000;0.000} ms stored, 0.000 ms applied";
    }

    private async Task ApplyVideoDeviceChangeAsync()
    {
        _suppressPreviewRestart = true;
        try
        {
            await AutoSelectAudioAsync();
        }
        finally
        {
            _suppressPreviewRestart = false;
        }

        if (_settingsReady)
        {
            SaveSettings();
        }

        if (!IsRecording)
        {
            await RestartPreviewAsync();
        }
    }

    private async Task AutoSelectAudioAsync()
    {
        if (SelectedVideoDevice is null) return;
        try
        {
            var match = await _audioCapture.FindMatchingDeviceAsync(SelectedVideoDevice);
            if (match is not null)
            {
                SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == match.Id) ?? match;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio auto-select failed");
        }
    }

    // ---- Preview ------------------------------------------------------------

    private Task RunPreviewTransitionAsync(Func<Task> operation)
    {
        if (_previewTransitionInProgress)
        {
            return _previewTransitionTask;
        }

        _previewTransitionInProgress = true;
        OnPropertyChanged(nameof(CanConfigureCapture));
        StartRecordingCommand.NotifyCanExecuteChanged();
        _previewTransitionTask = CompletePreviewTransitionAsync(operation);
        return _previewTransitionTask;
    }

    private async Task CompletePreviewTransitionAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            ReportError("Failed to update preview", ex);
        }
        finally
        {
            _previewTransitionInProgress = false;
            OnPropertyChanged(nameof(CanConfigureCapture));
            StartRecordingCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RestartPreviewAsync()
    {
        if (IsRecording || SelectedVideoDevice is null) return;
        await StopPreviewInternalAsync();
        await StartPreviewAsync();
    }

    private async Task StartPreviewAsync()
    {
        if (IsPreviewRunning || SelectedVideoDevice is null) return;

        try
        {
            _previewStartedTimestamp100ns = CaptureMonotonicClock.GetTimestamp100ns();
            Interlocked.Exchange(ref _lastPreviewFrameTimestamp100ns, 0);
            // A terminal health snapshot belongs to the capture session that
            // published it. Reset it before opening so an old zero-timestamp or
            // faulted snapshot cannot mark a successfully reopened preview lost.
            _latestVideoHealth = new CaptureHealth
            {
                State = CaptureHealthState.Starting,
                Timestamp100ns = _previewStartedTimestamp100ns,
            };
            ApplyConfigurationToCaptureService(CurrentVideoInputConfiguration);
            await _videoCapture.StartAsync(SelectedVideoDevice);
            RefreshVideoInputFormatStatus();
            if (SelectedAudioDevice is not null)
            {
                await _audioCapture.StartAsync(SelectedAudioDevice);
            }

            _previewCts = new CancellationTokenSource();
            _previewSubscription = _videoCapture.Subscribe(CaptureSubscriptionOptions.Preview(capacity: 2));
            _previewTask = Task.Run(
                () => PreviewLoopAsync(_previewSubscription, _previewCts.Token),
                CancellationToken.None);
            IsPreviewRunning = true;
            RefreshPreviewFreshness();
            AppendLog($"Preview started ({SelectedVideoDevice.Name})");
            await _audioMonitor.SyncAsync(AudioMonitoringEnabled);
        }
        catch (Exception ex)
        {
            // Starting video, audio, the preview subscription and monitoring is a
            // single logical operation. Roll back a partially opened device set so
            // the next retry does not inherit a hidden capture pump.
            try
            {
                await StopPreviewInternalAsync();
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(
                    cleanupException,
                    "Preview startup rollback could not stop every capture component");
            }
            ReportError("Failed to start preview", ex);
        }
    }

    private async Task StopPreviewInternalAsync()
    {
        await _audioMonitor.SyncAsync(false);

        if (_previewCts is not null)
        {
            await _previewCts.CancelAsync();
        }
        if (_previewTask is not null)
        {
            try { await _previewTask; } catch (OperationCanceledException) { }
        }
        _previewCts?.Dispose();
        _previewCts = null;
        _previewTask = null;
        if (_previewSubscription is not null)
        {
            await _previewSubscription.DisposeAsync();
            _previewSubscription = null;
        }

        if (_videoCapture.IsCapturing) await _videoCapture.StopAsync();
        if (_audioCapture.IsCapturing) await _audioCapture.StopAsync();
        IsPreviewRunning = false;
        PreviewHealthText = "";
        IsPreviewHealthVisible = false;
        IsPreviewLost = false;
    }

    partial void OnAudioMonitoringEnabledChanged(bool value)
    {
        _ = _audioMonitor.SyncAsync(value);
        SaveSettings();
    }

    partial void OnMonitorVolumePercentChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 200);
        if (clamped != value)
        {
            MonitorVolumePercent = clamped;
            return;
        }

        _audioMonitor.Volume = clamped / 100f;
        OnPropertyChanged(nameof(MonitorVolumeLabel));
        SaveSettings();
    }

    private async Task PreviewLoopAsync(ChannelReader<VideoFrame> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct))
            {
                Interlocked.Exchange(ref _lastPreviewFrameTimestamp100ns, frame.Timestamp100ns);
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null) return;

                if (PreviewYadif2xEnabled
                    && _previewBob.TryDeinterlace(frame.Data, frame.Format, out var first, out var second))
                {
                    var w = frame.Format.Width;
                    var h = frame.Format.Height;
                    // Pace the two bob frames so each is painted: writing both in one
                    // dispatcher callback only leaves the second field on screen (~25p).
                    var fieldPeriod = FieldPeriod(frame.Format.FrameRate);

                    await dispatcher.InvokeAsync(
                        () => RenderPixels(w, h, first), DispatcherPriority.Render);
                    try
                    {
                        await Task.Delay(fieldPeriod, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    await dispatcher.InvokeAsync(
                        () => RenderPixels(w, h, second), DispatcherPriority.Render);
                }
                else
                {
                    await dispatcher.InvokeAsync(
                        () => RenderFrame(frame), DispatcherPriority.Render);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                PreviewHealthText = $"Preview failed: {ex.Message}";
                IsPreviewHealthVisible = true;
                IsPreviewLost = true;
            });
        }
    }

    /// <summary>Half of one capture frame — duration of one field for 2× bob preview.</summary>
    private static TimeSpan FieldPeriod(double frameRate)
    {
        var fps = frameRate > 1 ? frameRate : 25;
        return TimeSpan.FromSeconds(0.5 / fps);
    }

    private void RenderFrame(VideoFrame frame)
        => RenderPixels(frame.Format.Width, frame.Format.Height, frame.Data);

    private void RenderPixels(int width, int height, byte[] bgr24)
    {
        if (_previewBitmap is null
            || _previewBitmap.PixelWidth != width
            || _previewBitmap.PixelHeight != height)
        {
            _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
            PreviewSource = _previewBitmap;
        }

        var stride = width * 3;
        _previewBitmap.WritePixels(
            new Int32Rect(0, 0, width, height), bgr24, stride, 0);
    }

    // ---- Transport ------------------------------------------------------------

    private bool CanUseTransport()
        => !IsRecording || !DisableTransportDuringRecording;

    private bool CanUseMediaKeys()
        => MediaKeysControlTransport && CanUseTransport();

    [RelayCommand(CanExecute = nameof(CanUseTransport))]
    private Task PlayAsync() => SendTransportAsync(TransportCommand.Play);

    [RelayCommand(CanExecute = nameof(CanUseTransport))]
    private Task StopAsync() => SendTransportAsync(TransportCommand.Stop);

    [RelayCommand(CanExecute = nameof(CanUseTransport))]
    private Task PauseAsync() => SendTransportAsync(TransportCommand.Pause);

    [RelayCommand(CanExecute = nameof(CanUseTransport))]
    private Task FrameStepForwardAsync() => SendTransportAsync(TransportCommand.FrameStepForward);

    [RelayCommand(CanExecute = nameof(CanUseTransport))]
    private Task FrameStepReverseAsync() => SendTransportAsync(TransportCommand.FrameStepReverse);

    [RelayCommand(CanExecute = nameof(CanUseTransport))]
    private Task ResetCtlAsync() => SendTransportAsync(TransportCommand.Timer1Reset);

    [RelayCommand(CanExecute = nameof(CanUseTransport))]
    private Task FastForwardAsync() => SendTransportAsync(TransportCommand.FastForward);

    [RelayCommand(CanExecute = nameof(CanUseTransport))]
    private Task RewindAsync() => SendTransportAsync(TransportCommand.Rewind);

    [RelayCommand(CanExecute = nameof(CanUseTransport))]
    private Task EjectAsync() => SendTransportAsync(TransportCommand.Eject);

    [RelayCommand(CanExecute = nameof(CanUseTransport))]
    private Task ToggleStandbyAsync() =>
        SendTransportAsync(IsStandbyOn ? TransportCommand.StandbyOff : TransportCommand.StandbyOn);

    [RelayCommand(CanExecute = nameof(CanUseMediaKeys))]
    private Task MediaPlayPauseAsync() =>
        SendTransportAsync(_currentTransport == TransportState.Playing
            ? TransportCommand.Pause
            : TransportCommand.Play);

    [RelayCommand(CanExecute = nameof(CanUseMediaKeys))]
    private Task MediaStopAsync() => SendTransportAsync(TransportCommand.Stop);

    [RelayCommand(CanExecute = nameof(CanUseMediaKeys))]
    private Task MediaFastForwardAsync() => SendTransportAsync(TransportCommand.FastForward);

    [RelayCommand(CanExecute = nameof(CanUseMediaKeys))]
    private Task MediaRewindAsync() => SendTransportAsync(TransportCommand.Rewind);

    [RelayCommand]
    private void BeginEditLtc()
    {
        if (!CanUseTransport() || IsEditingLtc || IsEditingCtl || IsEditingVitc) return;
        LtcEditText = LtcText.StartsWith("--", StringComparison.Ordinal) || LtcText.StartsWith('-')
            ? "00:00:00:00"
            : LtcText;
        IsEditingLtc = true;
    }

    [RelayCommand]
    private void CancelEditLtc()
    {
        IsEditingLtc = false;
    }

    [RelayCommand]
    private async Task CommitGoToLtcAsync()
    {
        if (!CanUseTransport()) return;

        if (!Timecode.TryParse(LtcEditText, out var timecode))
        {
            ReportError(
                "Invalid timecode. Use HH:MM:SS:FF (e.g. 00:01:00:00 for 1 minute), or MM:SS:FF.",
                null);
            return;
        }

        // Show the normalized value so hours vs minutes mistakes are obvious.
        LtcEditText = timecode.ToString();

        try
        {
            await _vtr.CueUpAsync(timecode, CueUpTimerMode.TimeCode);
            AppendLog($"Transport: Cue Up {timecode} (LTC / TIME CODE mode)");
            IsEditingLtc = false;
        }
        catch (UnsupportedCommandException ex)
        {
            ReportError("Cue Up not supported by this device", ex);
        }
        catch (Exception ex)
        {
            ReportError($"Cue Up to {timecode} failed", ex);
        }
    }

    [RelayCommand]
    private void BeginEditVitc()
    {
        if (!CanUseTransport() || IsEditingVitc || IsEditingLtc || IsEditingCtl) return;
        VitcEditText = VitcText.StartsWith("--", StringComparison.Ordinal) || VitcText.StartsWith('-')
            ? "00:00:00:00"
            : VitcText;
        IsEditingVitc = true;
    }

    [RelayCommand]
    private void CancelEditVitc()
    {
        IsEditingVitc = false;
    }

    [RelayCommand]
    private async Task CommitGoToVitcAsync()
    {
        if (!CanUseTransport()) return;

        if (!Timecode.TryParse(VitcEditText, out var timecode))
        {
            ReportError(
                "Invalid timecode. Use HH:MM:SS:FF (e.g. 00:01:00:00 for 1 minute), or MM:SS:FF.",
                null);
            return;
        }

        VitcEditText = timecode.ToString();

        try
        {
            // Sony Cue Up addresses the shared TIME CODE domain. A VITC reading is
            // therefore cued with the same timer mode as an LTC reading.
            await _vtr.CueUpAsync(timecode, CueUpTimerMode.TimeCode);
            AppendLog($"Transport: Cue Up {timecode} (VITC / TIME CODE mode)");
            IsEditingVitc = false;
        }
        catch (UnsupportedCommandException ex)
        {
            ReportError("Cue Up not supported by this device", ex);
        }
        catch (Exception ex)
        {
            ReportError($"VITC Cue Up to {timecode} failed", ex);
        }
    }

    [RelayCommand]
    private void BeginEditCtl()
    {
        if (!CanUseTransport() || IsEditingCtl || IsEditingLtc || IsEditingVitc) return;
        CtlEditText = CtlText.StartsWith("--", StringComparison.Ordinal)
            ? "00:00:00:00"
            : CtlText;
        IsEditingCtl = true;
    }

    [RelayCommand]
    private void CancelEditCtl()
    {
        IsEditingCtl = false;
    }

    [RelayCommand]
    private async Task CommitGoToCtlAsync()
    {
        if (!CanUseTransport()) return;

        // Wrap display is non-negative; signed display may use a leading '-'.
        var allowNegative = !Ctl24HourWrap;
        if (!Timecode.TryParse(CtlEditText, out var timecode, allowNegative))
        {
            ReportError(
                allowNegative
                    ? "Invalid CTL. Use HH:MM:SS:FF or −HH:MM:SS:FF (signed), or MM:SS:FF."
                    : "Invalid CTL. Use HH:MM:SS:FF (24h wrap), or MM:SS:FF.",
                null);
            return;
        }

        CtlEditText = timecode.ToString();

        try
        {
            await _vtr.CueUpAsync(timecode, CueUpTimerMode.Timer1);
            AppendLog($"Transport: Cue Up {timecode} (CTL / TIMER-1 mode)");
            IsEditingCtl = false;
        }
        catch (UnsupportedCommandException ex)
        {
            ReportError("Cue Up not supported by this device", ex);
        }
        catch (Exception ex)
        {
            ReportError($"CTL Cue Up to {timecode} failed", ex);
        }
    }

    /// <summary>
    /// Resolve-style J/K/L. Returns true when the key was handled (caller should mark Handled).
    /// </summary>
    public async Task<bool> HandleJklKeyAsync(Key key)
    {
        if (!CanUseMediaKeys()) return false;
        if (key is not (Key.J or Key.K or Key.L)) return false;

        _jklStep = key switch
        {
            Key.K => JklShuttleSteps.ApplyK(_jklStep),
            Key.L => JklShuttleSteps.ApplyL(_jklStep),
            _ => JklShuttleSteps.ApplyJ(_jklStep),
        };

        var action = JklShuttleSteps.ToAction(_jklStep, CurrentMaxShuttleRate);
        switch (action.Kind)
        {
            case JklActionKind.Stop:
                await SendTransportAsync(TransportCommand.Stop, fromJkl: true);
                break;
            case JklActionKind.Play:
                await SendTransportAsync(TransportCommand.Play, fromJkl: true);
                break;
            case JklActionKind.Shuttle:
                await SendJklShuttleAsync(action.Forward, action.PlayRate);
                break;
        }

        return true;
    }

    /// <summary>
    /// NLE-style frame step: <c>,</c> reverse, <c>.</c> forward.
    /// Returns true when the key was handled.
    /// </summary>
    public async Task<bool> HandleFrameStepKeyAsync(Key key)
    {
        if (!CanUseTransport()) return false;
        if (key is not (Key.OemComma or Key.OemPeriod)) return false;

        await SendTransportAsync(key == Key.OemPeriod
            ? TransportCommand.FrameStepForward
            : TransportCommand.FrameStepReverse);
        return true;
    }

    private async Task SendJklShuttleAsync(bool forward, double playRate)
    {
        if (!CanUseTransport()) return;

        var speed = VariableSpeedEncoding.FromPlayRate(
            playRate, VariableSpeedMode.Shuttle, CurrentMaxShuttleRate);
        try
        {
            await _vtr.SendVariableSpeedAsync(VariableSpeedMode.Shuttle, forward, speed);
            var signed = forward ? playRate : -playRate;
            AppendLog($"Transport: JKL {signed:+0.##;-0.##;0}× (N={speed})");
        }
        catch (UnsupportedCommandException ex)
        {
            ReportError("Shuttle not supported by this device", ex);
        }
        catch (Exception ex)
        {
            ReportError("JKL shuttle command failed", ex);
        }
    }

    private void NotifyTransportCanExecuteChanged()
    {
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        FrameStepForwardCommand.NotifyCanExecuteChanged();
        FrameStepReverseCommand.NotifyCanExecuteChanged();
        FastForwardCommand.NotifyCanExecuteChanged();
        RewindCommand.NotifyCanExecuteChanged();
        EjectCommand.NotifyCanExecuteChanged();
        ToggleStandbyCommand.NotifyCanExecuteChanged();
        MediaPlayPauseCommand.NotifyCanExecuteChanged();
        MediaStopCommand.NotifyCanExecuteChanged();
        MediaFastForwardCommand.NotifyCanExecuteChanged();
        MediaRewindCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUseTransportButtons));
    }

    partial void OnMediaKeysControlTransportChanged(bool value)
    {
        MediaPlayPauseCommand.NotifyCanExecuteChanged();
        MediaStopCommand.NotifyCanExecuteChanged();
        MediaFastForwardCommand.NotifyCanExecuteChanged();
        MediaRewindCommand.NotifyCanExecuteChanged();
    }

    partial void OnJogShuttleModeChanged(VariableSpeedMode value)
    {
        OnPropertyChanged(nameof(IsJogMode));
        OnPropertyChanged(nameof(IsShuttleMode));
        UpdateWheelSpeedLabel();
        if (!_suppressWheelSend && Math.Abs(WheelPosition) >= 0.02)
        {
            _ = SendWheelAsync(force: true);
        }
    }

    partial void OnWheelPositionChanged(double value)
    {
        UpdateWheelSpeedLabel();
        if (_suppressWheelSend) return;
        _ = SendWheelAsync(force: false);
    }

    private void UpdateWheelSpeedLabel()
    {
        var (forward, speed) = VariableSpeedEncoding.FromWheel(
            WheelPosition, JogShuttleMode, CurrentMaxShuttleRate);
        var rate = VariableSpeedEncoding.ToPlayRate(speed);
        if (!forward && rate > 0) rate = -rate;
        WheelSpeedLabel = $"{rate:+0.00;-0.00;0.00}×";
    }

    private async Task SendWheelAsync(bool force)
    {
        if (!CanUseTransport() || !_vtr.IsConnected) return;

        // Coalesce: never drop the latest cursor position. A prior implementation
        // returned early during the 60ms throttle / in-flight await, so a fast sweep
        // to the dial edge left the deck stuck at the last successfully sent speed.
        if (_wheelSendInFlight)
        {
            _wheelSendPending = true;
            return;
        }

        _wheelSendInFlight = true;
        try
        {
            do
            {
                _wheelSendPending = false;
                if (_suppressWheelSend) break;

                var (forward, speed) = VariableSpeedEncoding.FromWheel(
                    WheelPosition, JogShuttleMode, CurrentMaxShuttleRate);
                if (!force
                    && speed == _lastWheelSpeed
                    && forward == _lastWheelForward
                    && JogShuttleMode == _lastWheelMode)
                {
                    break;
                }

                var elapsed = (DateTimeOffset.UtcNow - _lastWheelSend).TotalMilliseconds;
                if (!force && elapsed < 60)
                {
                    await Task.Delay(Math.Max(1, 60 - (int)elapsed));
                    // Re-read WheelPosition after the wait (cursor may have moved further).
                    _wheelSendPending = true;
                    force = false;
                    continue;
                }

                if (_suppressWheelSend) break;

                (forward, speed) = VariableSpeedEncoding.FromWheel(
                    WheelPosition, JogShuttleMode, CurrentMaxShuttleRate);
                if (!force
                    && speed == _lastWheelSpeed
                    && forward == _lastWheelForward
                    && JogShuttleMode == _lastWheelMode)
                {
                    break;
                }

                _lastWheelSend = DateTimeOffset.UtcNow;
                _lastWheelSpeed = speed;
                _lastWheelForward = forward;
                _lastWheelMode = JogShuttleMode;

                try
                {
                    await _vtr.SendVariableSpeedAsync(JogShuttleMode, forward, speed);
                    // Debug only — AppendLog on every nudge freezes the UI ListBox mid-drag.
                    if (speed == VariableSpeedEncoding.Still)
                    {
                        _logger.LogDebug("Transport: {Mode} still", JogShuttleMode);
                    }
                    else
                    {
                        var rate = VariableSpeedEncoding.ToPlayRate(speed);
                        _logger.LogDebug(
                            "Transport: {Mode} {Sign}{Rate:0.##}× (N={Speed})",
                            JogShuttleMode, forward ? "+" : "-", rate, speed);
                    }
                }
                catch (UnsupportedCommandException ex)
                {
                    _wheelSendPending = false;
                    ReportError($"{JogShuttleMode} not supported by this device", ex);
                    break;
                }
                catch (Exception ex)
                {
                    _wheelSendPending = false;
                    ReportError($"{JogShuttleMode} command failed", ex);
                    break;
                }

                force = false;
            } while (_wheelSendPending && !_suppressWheelSend);
        }
        finally
        {
            _wheelSendInFlight = false;
            if (_wheelSendPending && !_suppressWheelSend)
            {
                _ = SendWheelAsync(force: false);
            }
        }
    }

    /// <summary>Snap the wheel to center and Stop the deck (mouse release).</summary>
    public async Task ReleaseJogShuttleWheelAsync()
    {
        // MouseUp + LostMouseCapture both fire; only the first release should Stop.
        var epoch = Interlocked.Increment(ref _wheelReleaseEpoch);
        _suppressWheelSend = true;
        _wheelSendPending = false;
        try
        {
            WheelPosition = 0;
            UpdateWheelSpeedLabel();
            _lastWheelSpeed = 255; // force next drag to send
        }
        finally
        {
            _suppressWheelSend = false;
        }

        if (!CanUseTransport()) return;
        if (epoch != Volatile.Read(ref _wheelReleaseEpoch)) return;
        await SendTransportAsync(TransportCommand.Stop);
    }

    private async Task SendTransportAsync(TransportCommand command, bool fromJkl = false)
    {
        if (!CanUseTransport()) return;

        if (!fromJkl)
        {
            _jklStep = command switch
            {
                TransportCommand.Play => 1,
                TransportCommand.Stop => 0,
                _ => 0,
            };
        }

        try
        {
            await _vtr.SendTransportCommandAsync(command);
            AppendLog($"Transport: {command}");
        }
        catch (UnsupportedCommandException ex)
        {
            ReportError($"Command {command} not supported by this device", ex);
        }
        catch (Exception ex)
        {
            ReportError($"Transport command {command} failed", ex);
        }
    }

    // ---- Recording ------------------------------------------------------------

    private bool CanStartRecording()
        => !IsRecording
           && !_previewTransitionInProgress
           && !_recordingStartInProgress
           && SelectedRecordingProfile is not null
           && HasOutputDirectory
           && CanRecordWithVideoInputFormat;

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private async Task StartRecordingAsync()
    {
        if (IsRecording
            || _previewTransitionInProgress
            || _recordingStartInProgress
            || SelectedRecordingProfile is null)
        {
            return;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _recordingStartCompletion = completion;
        _recordingStartInProgress = true;
        OnPropertyChanged(nameof(CanConfigureCapture));
        StartRecordingCommand.NotifyCanExecuteChanged();
        BrowseOutputDirectoryCommand.NotifyCanExecuteChanged();
        OpenVideoSettingsCommand.NotifyCanExecuteChanged();
        OpenAudioSettingsCommand.NotifyCanExecuteChanged();
        try
        {
            await StartRecordingCoreAsync();
        }
        finally
        {
            _recordingStartInProgress = false;
            if (ReferenceEquals(_recordingStartCompletion, completion))
            {
                _recordingStartCompletion = null;
            }
            completion.TrySetResult(true);
            OnPropertyChanged(nameof(CanConfigureCapture));
            StartRecordingCommand.NotifyCanExecuteChanged();
            BrowseOutputDirectoryCommand.NotifyCanExecuteChanged();
            OpenVideoSettingsCommand.NotifyCanExecuteChanged();
            OpenAudioSettingsCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task StartRecordingCoreAsync()
    {
        // Freeze the profile across preview/preflight/autoplay awaits. Observable
        // selection properties can change between continuations even though the
        // command gate checked them before entering this method.
        var profile = SelectedRecordingProfile;
        if (profile is null) return;

        if (!HasOutputDirectory)
        {
            ReportError("Choose a save location via File → Choose save location… before recording.", null);
            return;
        }
        if (!HasExplicitVideoInputConfiguration)
        {
            ReportError(
                "Select the video input standard and scan mode in Connections before recording.",
                null);
            return;
        }

        if (!IsPreviewRunning)
        {
            await RunPreviewTransitionAsync(StartPreviewAsync);
            if (!IsPreviewRunning) return; // preview failed; error already reported
        }

        RefreshVideoInputFormatStatus();
        if (!CanRecordWithVideoInputFormat)
        {
            ReportError(VideoInputFormatStatusText, null);
            return;
        }

        // Preflight: surface workflow warnings (missing ffmpeg, tape out, not playing, ...)
        // and let the user confirm or cancel before anything is written.
        var warnings = _session.PreflightWarnings(AutoPlayOnRecord, OutputDirectory);
        if (warnings.Count > 0)
        {
            foreach (var warning in warnings)
            {
                AppendLog($"WARNING  {warning}");
            }
            if (!ConfirmPreflight(warnings))
            {
                AppendLog("Recording cancelled (preflight warnings not confirmed)");
                return;
            }
        }

        try
        {
            if (AutoPlayOnRecord && IsVtrConnected)
            {
                var playing = await _session.EnsurePlayingAsync();
                if (!playing)
                {
                    AppendLog("WARNING  Auto-play could not confirm playback; recording anyway");
                }
            }

            var outputPath = await _session.StartRecordingAsync(
                OutputDirectory,
                profile,
                SelectedVideoDevice?.Name,
                new CaptureSessionStartOptions
                {
                    VideoDeviceStableId = SelectedVideoDevice?.Id,
                    AudioDeviceStableId = SelectedAudioDevice?.Id,
                    Recording = new RecordingStartOptions
                    {
                        AudioOffset100ns = EffectiveAudioOffset100ns,
                        IsCalibrated = IsAvCalibrationCalibrated,
                        CalibrationKey = CurrentAvCalibrationKey,
                        CalibratedAt = AvCalibrationMeasuredAt,
                    },
                    VideoFormatMismatchAcknowledged = IsVideoFormatMismatchAcknowledged,
                    VideoFormatAcknowledgmentKey = VideoFormatAcknowledgmentKey,
                });
            IsRecording = true;
            AppendLog($"Recording started: {outputPath}");
        }
        catch (Exception ex)
        {
            ReportError("Failed to start recording", ex);
        }
    }

    /// <summary>Modal confirmation for preflight warnings. Overridable for tests.</summary>
    internal Func<IReadOnlyList<string>, bool>? PreflightConfirmationOverride { get; set; }

    private bool ConfirmPreflight(IReadOnlyList<string> warnings)
    {
        if (PreflightConfirmationOverride is not null)
        {
            return PreflightConfirmationOverride(warnings);
        }

        var message = "The following issues were detected:\n\n"
            + string.Join("\n", warnings.Select(w => "• " + w))
            + "\n\nStart recording anyway?";
        return MessageBox.Show(message, "Recording preflight",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        if (!IsRecording) return;
        try
        {
            await _session.StopRecordingAsync();
            IsRecording = false;
            AppendLog("Recording stopped (sidecar metadata written)");
        }
        catch (Exception ex)
        {
            ReportError("Failed to stop recording", ex);
        }
    }

    [RelayCommand]
    private void DismissError() => LastError = "";

    [RelayCommand(CanExecute = nameof(CanBrowseOutputDirectory))]
    private void BrowseOutputDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (HasOutputDirectory)
        {
            dialog.InitialDirectory = OutputDirectory;
        }
        if (dialog.ShowDialog() == true)
        {
            OutputDirectory = dialog.FolderName;
            SaveSettings();
        }
    }

    private bool CanBrowseOutputDirectory() => !IsRecording && !_recordingStartInProgress;

    private bool CanEditRecordingSettings() => !IsRecording && !_recordingStartInProgress;

    public bool CanConfigureCapture
        => !IsRecording && !_previewTransitionInProgress && !_recordingStartInProgress;

    partial void OnOutputDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(HasOutputDirectory));
        OnPropertyChanged(nameof(OutputDirectoryDisplay));
        StartRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRecordingProfileChanged(RecordingProfile? value)
    {
        OnPropertyChanged(nameof(VideoSettingsSummary));
        RefreshStatusBarCodec();
        StartRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRecordingChanged(bool value)
    {
        StartRecordingCommand.NotifyCanExecuteChanged();
        BrowseOutputDirectoryCommand.NotifyCanExecuteChanged();
        OpenVideoSettingsCommand.NotifyCanExecuteChanged();
        OpenAudioSettingsCommand.NotifyCanExecuteChanged();
        NotifyTransportCanExecuteChanged();
        OnPropertyChanged(nameof(CanConfigureCapture));
    }

    partial void OnDisableTransportDuringRecordingChanged(bool value)
        => NotifyTransportCanExecuteChanged();

    private void UpdateAudioMeters()
    {
        if (!_audioCapture.IsCapturing)
        {
            _linearMeterLeft = 0;
            _linearMeterRight = 0;
            AudioLevelLeft = 0;
            AudioLevelRight = 0;
            return;
        }

        // PeakLevels consumes held peaks since the last tick; release softens the fall on
        // linear amplitude, then map to −60…0 dBFS for the ProgressBar (matches tick legend).
        const double release = 0.75;
        var peaks = _audioCapture.PeakLevels;
        var left = peaks.Count > 0 ? peaks[0] : 0;
        var right = peaks.Count > 1 ? peaks[1] : left;
        _linearMeterLeft = Math.Max(left, _linearMeterLeft * release);
        _linearMeterRight = Math.Max(right, _linearMeterRight * release);
        AudioLevelLeft = LinearPeakToMeterDb(_linearMeterLeft);
        AudioLevelRight = LinearPeakToMeterDb(_linearMeterRight);
    }

    /// <summary>Maps linear peak 0…1 to meter fill 0…1 for a −60…0 dBFS scale.</summary>
    private static double LinearPeakToMeterDb(double peak)
    {
        const double floorDb = -60.0;
        if (peak <= 0) return 0;
        var db = 20.0 * Math.Log10(peak);
        return Math.Clamp((db - floorDb) / -floorDb, 0.0, 1.0);
    }

    // ---- Event handlers ---------------------------------------------------------

    private void OnVtrStatusChanged(object? sender, VtrStatus status)
    {
        RunOnUi(() =>
        {
            IsVtrConnected = status.IsConnected;
            IsStandbyOn = status.IsConnected && status.Standby;
            _currentTransport = status.Transport;
            TransportStateText = status.Transport.ToString();
            StatusBarTransportText = status.IsConnected
                ? status.Transport.ToString()
                : "VTR offline";

            var flags = new List<string>();
            if (status.TapeOut) flags.Add("TAPE OUT");
            if (status.IsLocal) flags.Add("LOCAL");
            if (status.ServoLock) flags.Add("SERVO LOCK");
            if (status.Standby) flags.Add("STANDBY");
            if (status.RecordInhibited) flags.Add("REC INHIBIT");
            if (status.NearEndOfTape) flags.Add("NEAR EOT");
            if (status.EndOfTape) flags.Add("EOT");
            if (status.SystemAlarm) flags.Add("SYS ALARM");
            if (status.ServoAlarm) flags.Add("SVO ALARM");
            StatusFlagsText = string.Join("  ", flags);
        });
    }

    private void OnVtrTimeChanged(object? sender, TimeInformation time)
    {
        _latestTimeInformation = time;
        RunOnUi(() => RefreshTimeInformation(DateTimeOffset.UtcNow));
    }

    private void OnVtrLinkHealthChanged(object? sender, VtrLinkHealth health)
    {
        _latestVtrLinkHealth = health;
        RunOnUi(() =>
        {
            if (health.State is VtrLinkState.Lost or VtrLinkState.Disconnected)
            {
                IsVtrConnected = false;
            }
            else if (health.State == VtrLinkState.Online)
            {
                // Link recovery may be observed on a time poll before the next
                // status-sense response; restore connectivity immediately.
                IsVtrConnected = _vtr.IsConnected;
            }
            RefreshTimeInformation(DateTimeOffset.UtcNow);
        });
    }

    private void OnVideoCaptureHealthChanged(object? sender, CaptureHealthEventArgs args)
    {
        _latestVideoHealth = args.Health;
        RunOnUi(RefreshPreviewFreshness);
    }

    private void RefreshFreshnessIndicators()
    {
        RefreshTimeInformation(DateTimeOffset.UtcNow);
        RefreshPreviewFreshness();
        RefreshVideoInputFormatStatus();
    }

    private void RefreshTimeInformation(DateTimeOffset now)
    {
        var time = _latestTimeInformation;
        var nowTimestamp100ns = CaptureMonotonicClock.GetTimestamp100ns();
        var ctl = time.CtlObservation;
        var ltc = time.LtcObservation;
        var vitc = time.VitcObservation;
        var linkUnavailable = _latestVtrLinkHealth.State is VtrLinkState.Disconnected or VtrLinkState.Lost;

        var ctlFreshness = linkUnavailable
            ? ObservationFreshness.Lost
            : ctl?.FreshnessAt(nowTimestamp100ns, now) ?? ObservationFreshness.Unavailable;
        var ltcFreshness = linkUnavailable
            ? ObservationFreshness.Lost
            : ltc?.FreshnessAt(nowTimestamp100ns, now) ?? ObservationFreshness.Unavailable;
        var vitcFreshness = linkUnavailable
            ? ObservationFreshness.Lost
            : vitc?.FreshnessAt(nowTimestamp100ns, now) ?? ObservationFreshness.Unavailable;

        CtlText = ctl is { } ctlValue && ctlFreshness != ObservationFreshness.Lost
            ? Timecode.FormatCtlDisplay(ctlValue.Value, Ctl24HourWrap)
            : "--:--:--:--";
        LtcText = ltc is { } ltcValue && ltcFreshness != ObservationFreshness.Lost
            ? ltcValue.Value.ToString()
            : "--:--:--:--";
        VitcText = vitc is { } vitcValue && vitcFreshness != ObservationFreshness.Lost
            ? vitcValue.Value.ToString()
            : "--:--:--:--";

        CtlFreshnessText = DescribeFreshness(ctl, ctlFreshness, now, nowTimestamp100ns);
        LtcFreshnessText = DescribeFreshness(ltc, ltcFreshness, now, nowTimestamp100ns);
        VitcFreshnessText = DescribeFreshness(vitc, vitcFreshness, now, nowTimestamp100ns);

        var userBits = SelectBestUserBits(time, now, nowTimestamp100ns);
        var userBitsFreshness = linkUnavailable
            ? ObservationFreshness.Lost
            : userBits?.FreshnessAt(nowTimestamp100ns, now) ?? ObservationFreshness.Unavailable;
        UserBitsText = userBits is { } bits && userBitsFreshness != ObservationFreshness.Lost
            ? bits.Value.ToString()
            : "-- -- -- --";
        UserBitsFreshnessText = DescribeFreshness(
            userBits, userBitsFreshness, now, nowTimestamp100ns);

        var primary = time.GetPrimaryObservation(now, nowTimestamp100ns);
        var linkState = _latestVtrLinkHealth.State;
        (VtrLinkHealthText, IsVtrLinkStaleOrLost, IsVtrLinkLost) = linkState switch
        {
            VtrLinkState.Lost => ("VTR CONNECTION LOST — retrying", true, true),
            VtrLinkState.Stale => ("VTR RESPONSE STALE", true, false),
            VtrLinkState.Connecting => ("Waiting for VTR response…", true, false),
            VtrLinkState.Disconnected => ("VTR disconnected", true, true),
            _ when primary is null => ("Timecode unavailable or stale", true, false),
            _ => ("Time data current", false, false),
        };
    }

    private static TimeObservation<UserBits>? SelectBestUserBits(
        TimeInformation time,
        DateTimeOffset now,
        long nowTimestamp100ns)
    {
        var observations = new[] { time.LtcUserBitsObservation, time.VitcUserBitsObservation }
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .OrderBy(item => FreshnessRank(item.FreshnessAt(nowTimestamp100ns, now)))
            .ThenByDescending(item => item.ReceivedAt)
            .ToArray();
        return observations.Length == 0 ? null : observations[0];
    }

    private static int FreshnessRank(ObservationFreshness freshness) => freshness switch
    {
        ObservationFreshness.Fresh => 0,
        ObservationFreshness.Stale => 1,
        ObservationFreshness.Lost => 2,
        _ => 3,
    };

    private static string DescribeFreshness<T>(
        TimeObservation<T>? observation,
        ObservationFreshness freshness,
        DateTimeOffset now,
        long nowTimestamp100ns)
    {
        if (observation is null) return "N/A";

        var source = observation.Value.Source switch
        {
            TimecodeSource.CorrectedLtc => "CORR",
            TimecodeSource.HoldVitc or TimecodeSource.HoldLtc => "HOLD",
            _ => "",
        };
        var age = observation.Value.AgeAt(nowTimestamp100ns, now);
        var state = freshness switch
        {
            ObservationFreshness.Fresh => "",
            ObservationFreshness.Stale => $"STALE {Math.Max(0, age.TotalSeconds):F1}s",
            ObservationFreshness.Lost => "LOST",
            _ => "N/A",
        };
        return string.Join(" · ", new[] { source, state }.Where(value => value.Length > 0));
    }

    private void RefreshPreviewFreshness()
    {
        if (!IsPreviewRunning)
        {
            PreviewHealthText = "";
            IsPreviewHealthVisible = false;
            IsPreviewLost = false;
            return;
        }

        var health = _latestVideoHealth;
        var healthIsCurrentSession = health.Timestamp100ns <= 0
                                     || health.Timestamp100ns >= _previewStartedTimestamp100ns;
        if (healthIsCurrentSession && health.State == CaptureHealthState.Faulted)
        {
            PreviewHealthText = string.IsNullOrWhiteSpace(health.Error)
                ? "VIDEO SIGNAL LOST"
                : $"VIDEO SIGNAL LOST — {health.Error}";
            IsPreviewHealthVisible = true;
            IsPreviewLost = true;
            return;
        }

        var now100ns = CaptureMonotonicClock.GetTimestamp100ns();
        // Health notifications are edge-triggered, while the preview loop records
        // every consumed frame. Always use the newest of those two monotonic points
        // so a previous recovery notification cannot later make a live preview stale.
        var lastFrame100ns = Math.Max(
            health.LastDeliveryTimestamp100ns ?? 0,
            Interlocked.Read(ref _lastPreviewFrameTimestamp100ns));
        // Never carry the previous capture session's delivery timestamp into a
        // newly opened preview. Before its first frame, age from this session's start.
        var reference100ns = Math.Max(_previewStartedTimestamp100ns, lastFrame100ns);
        var age = TimeSpan.FromTicks(Math.Max(0, now100ns - reference100ns));
        var freshness = MonotonicFreshness.Classify(
            reference100ns,
            now100ns,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(2));

        if (freshness == ObservationFreshness.Lost)
        {
            PreviewHealthText = $"VIDEO SIGNAL LOST — last frame {age.TotalSeconds:F1}s ago";
            IsPreviewHealthVisible = true;
            IsPreviewLost = true;
        }
        else if (freshness == ObservationFreshness.Stale
                 || healthIsCurrentSession && health.State == CaptureHealthState.Degraded)
        {
            PreviewHealthText = $"Preview stale — last frame {age.TotalSeconds:F1}s ago";
            IsPreviewHealthVisible = true;
            IsPreviewLost = false;
        }
        else
        {
            PreviewHealthText = "";
            IsPreviewHealthVisible = false;
            IsPreviewLost = false;
        }
    }

    private void OnRecordingStatusChanged(object? sender, RecordingStatus status)
    {
        RunOnUi(() =>
        {
            RecordingStatusText = FormatRecordingStatus(status);
            RecordingSyncStatusText = FormatRecordingSyncStatus(status);
            IsRecordingSyncStatusVisible = RecordingSyncStatusText.Length > 0;
            RecordingSyncStatusBrush = status.State == RecordingState.Faulted
                ? Brushes.IndianRed
                : status.SyncWarning
                  || !status.SyncTelemetryComplete
                  || status.State == RecordingState.Incomplete
                    ? Brushes.Gold
                    : Brushes.LightGray;
            IsRecording = status.State is RecordingState.Recording
                or RecordingState.Starting
                or RecordingState.Stopping;
            StatusBarRecordingText = status.State switch
            {
                RecordingState.Recording => "● REC",
                RecordingState.Starting => "Starting…",
                RecordingState.Stopping => "Stopping…",
                RecordingState.Incomplete => $"Incomplete: {status.StopReason}",
                RecordingState.Cancelled => "Cancelled",
                RecordingState.Faulted => "Faulted",
                _ => "Idle",
            };
            StatusBarElapsedText = status.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Stopping
                ? status.Elapsed.ToString(@"hh\:mm\:ss")
                : "—";
            if (status.State == RecordingState.Faulted && status.Error is not null)
            {
                ReportError(status.Error, null);
            }
        });
    }

    internal static string FormatRecordingStatus(RecordingStatus status)
    {
        var detail = string.IsNullOrWhiteSpace(status.Error) ? "" : $" — {status.Error}";
        return status.State switch
        {
            RecordingState.Recording =>
                $"Recording {status.Elapsed:hh\\:mm\\:ss} ({status.VideoFramesWritten} frames)",
            RecordingState.Incomplete => $"Incomplete: {status.StopReason}{detail}",
            RecordingState.Cancelled => $"Cancelled: {status.StopReason}{detail}",
            RecordingState.Faulted => $"Error: {status.StopReason}{detail}",
            _ => status.State.ToString(),
        };
    }

    internal static string FormatRecordingSyncStatus(RecordingStatus status)
    {
        if (status.State == RecordingState.Idle && status.StopReason == RecordingStopReason.None)
        {
            return "";
        }

        var residual = status.CurrentAvOffset is { } offset
            ? FormattableString.Invariant($"residual {offset.TotalMilliseconds:+0.0;-0.0;0.0} ms")
            : "residual measuring";
        var telemetry = status.SyncTelemetryComplete
            ? residual
            : "timestamp telemetry incomplete";
        var prefix = status.SyncWarning || !status.SyncTelemetryComplete
            ? "A/V SYNC WARNING"
            : "A/V sync";
        FormattableString text = $"{prefix} — {telemetry} · drift {status.EstimatedDriftPpm:+0.0;-0.0;0.0} ppm · correction {status.AppliedCorrectionPpm:+0.0;-0.0;0.0} ppm (required {status.RequiredCorrectionPpm:+0.0;-0.0;0.0})";
        return FormattableString.Invariant(text);
    }

    private void RefreshStatusBarCodec()
    {
        StatusBarCodecText = SelectedRecordingProfile is { } profile
            ? $"{profile.VideoCodec} / {profile.AudioCodecDisplayName} ({profile.Container})"
            : "—";
    }

    // ---- Helpers ------------------------------------------------------------------

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private void AppendLog(string message)
    {
        _logger.LogInformation("{Message}", message);
        RunOnUi(() =>
        {
            LogEntries.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (LogEntries.Count > 200) LogEntries.RemoveAt(LogEntries.Count - 1);
        });
    }

    /// <summary>Entry point for the global exception handlers in App.xaml.cs.</summary>
    public void ReportUnhandledException(string context, Exception exception)
        => ReportError(context, exception);

    private void ReportError(string message, Exception? ex)
    {
        if (ex is not null) _logger.LogError(ex, "{Message}", message);
        else _logger.LogError("{Message}", message);

        RunOnUi(() =>
        {
            LastError = ex is null ? message : $"{message}: {ex.Message}";
            LogEntries.Insert(0, $"{DateTime.Now:HH:mm:ss}  ERROR  {LastError}");
        });
    }
}
