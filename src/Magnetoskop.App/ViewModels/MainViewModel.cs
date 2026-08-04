using System.Collections.ObjectModel;
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

    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
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
    private WatchWindow? _watchWindow;
    private ConnectionsWindow? _connectionsWindow;
    private TransportState _currentTransport = TransportState.Unknown;
    /// <summary>JKL shuttle step: 0 stopped, +n forward, −n reverse.</summary>
    private int _jklStep;

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
        _vtr.CapabilityLearned += OnCapabilityLearned;
        _recorder.StatusChanged += OnRecordingStatusChanged;

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
    private bool _isEditingCtl;

    [ObservableProperty]
    private string _ctlEditText = "00:00:00:00";

    [ObservableProperty]
    private string _ltcText = "--:--:--:--";

    [ObservableProperty]
    private bool _isEditingLtc;

    [ObservableProperty]
    private string _ltcEditText = "00:00:00:00";

    [ObservableProperty]
    private string _vitcText = "--:--:--:--";

    [ObservableProperty]
    private string _userBitsText = "-- -- -- --";

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
    private CaptureDeviceInfo? _selectedVideoDevice;

    [ObservableProperty]
    private CaptureDeviceInfo? _selectedAudioDevice;

    /// <summary>When true the user picked an audio device manually; otherwise auto-select applies.</summary>
    [ObservableProperty]
    private bool _audioManuallySelected;

    /// <summary>When true, play the live capture input through the default output device.</summary>
    [ObservableProperty]
    private bool _audioMonitoringEnabled;

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

            await StartPreviewAsync();
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
        SaveSettings();
        try
        {
            await StopPreviewInternalAsync();
            if (_recorder.Status.State == RecordingState.Recording)
            {
                await _session.StopRecordingAsync();
            }
            await _vtr.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during shutdown");
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

    [RelayCommand]
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
    private async Task RefreshDevicesAsync()
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

    /// <summary>Prefer saved friendly name (stable), then id (DirectShow index), then default.</summary>
    private CaptureDeviceInfo? ResolveVideoDevice(string? savedId, string? savedName)
    {
        if (!string.IsNullOrWhiteSpace(savedName))
        {
            var byName = VideoDevices.FirstOrDefault(d =>
                string.Equals(d.Name, savedName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }

        if (!string.IsNullOrWhiteSpace(savedId))
        {
            var byId = VideoDevices.FirstOrDefault(d => d.Id == savedId);
            if (byId is not null) return byId;
        }

        return VideoDevices.FirstOrDefault(d => d.IsDefault) ?? VideoDevices.FirstOrDefault();
    }

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
        if (_settingsReady && !_suppressPreviewRestart)
        {
            SaveSettings();
        }

        if (_suppressPreviewRestart || IsRecording) return;

        if (value is not null && !AudioManuallySelected)
        {
            _ = ApplyVideoDeviceChangeAsync();
        }
        else
        {
            _ = RestartPreviewAsync();
        }
    }

    partial void OnSelectedAudioDeviceChanged(CaptureDeviceInfo? value)
    {
        if (_settingsReady && !_suppressPreviewRestart)
        {
            SaveSettings();
        }

        if (_suppressPreviewRestart || IsRecording) return;
        _ = RestartPreviewAsync();
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
            await _videoCapture.StartAsync(SelectedVideoDevice);
            if (SelectedAudioDevice is not null)
            {
                await _audioCapture.StartAsync(SelectedAudioDevice);
            }

            _previewCts = new CancellationTokenSource();
            var reader = _videoCapture.Subscribe(capacity: 2);
            _previewTask = Task.Run(() => PreviewLoopAsync(reader, _previewCts.Token), CancellationToken.None);
            IsPreviewRunning = true;
            AppendLog($"Preview started ({SelectedVideoDevice.Name})");
            await _audioMonitor.SyncAsync(AudioMonitoringEnabled);
        }
        catch (Exception ex)
        {
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

        if (_videoCapture.IsCapturing) await _videoCapture.StopAsync();
        if (_audioCapture.IsCapturing) await _audioCapture.StopAsync();
        IsPreviewRunning = false;
    }

    partial void OnAudioMonitoringEnabledChanged(bool value)
    {
        _ = _audioMonitor.SyncAsync(value);
        SaveSettings();
    }

    private async Task PreviewLoopAsync(ChannelReader<VideoFrame> reader, CancellationToken ct)
    {
        await foreach (var frame in reader.ReadAllAsync(ct))
        {
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
        if (!CanUseTransport() || IsEditingLtc || IsEditingCtl) return;
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
    private void BeginEditCtl()
    {
        if (!CanUseTransport() || IsEditingCtl || IsEditingLtc) return;
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

        var action = JklShuttleSteps.ToAction(_jklStep);
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

        var speed = VariableSpeedEncoding.FromPlayRate(playRate, VariableSpeedMode.Shuttle);
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
        var (forward, speed) = VariableSpeedEncoding.FromWheel(WheelPosition, JogShuttleMode);
        var rate = VariableSpeedEncoding.ToPlayRate(speed);
        if (!forward && rate > 0) rate = -rate;
        WheelSpeedLabel = $"{rate:+0.00;-0.00;0.00}×";
    }

    private async Task SendWheelAsync(bool force)
    {
        if (!CanUseTransport() || !_vtr.IsConnected) return;

        var (forward, speed) = VariableSpeedEncoding.FromWheel(WheelPosition, JogShuttleMode);
        var now = DateTimeOffset.UtcNow;
        if (!force
            && speed == _lastWheelSpeed
            && forward == _lastWheelForward
            && JogShuttleMode == _lastWheelMode)
        {
            return;
        }

        if (!force && (now - _lastWheelSend).TotalMilliseconds < 60)
        {
            return;
        }

        _lastWheelSend = now;
        _lastWheelSpeed = speed;
        _lastWheelForward = forward;
        _lastWheelMode = JogShuttleMode;

        try
        {
            await _vtr.SendVariableSpeedAsync(JogShuttleMode, forward, speed);
            if (speed == VariableSpeedEncoding.Still)
            {
                AppendLog($"Transport: {JogShuttleMode} still");
            }
            else
            {
                var rate = VariableSpeedEncoding.ToPlayRate(speed);
                AppendLog($"Transport: {JogShuttleMode} {(forward ? "+" : "-")}{rate:0.##}× (N={speed})");
            }
        }
        catch (UnsupportedCommandException ex)
        {
            ReportError($"{JogShuttleMode} not supported by this device", ex);
        }
        catch (Exception ex)
        {
            ReportError($"{JogShuttleMode} command failed", ex);
        }
    }

    /// <summary>Snap the wheel to center and Stop the deck (mouse release).</summary>
    public async Task ReleaseJogShuttleWheelAsync()
    {
        _suppressWheelSend = true;
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
        => !IsRecording && SelectedRecordingProfile is not null && HasOutputDirectory;

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private async Task StartRecordingAsync()
    {
        if (IsRecording || SelectedRecordingProfile is null) return;
        if (!HasOutputDirectory)
        {
            ReportError("Choose a save location via File → Choose save location… before recording.", null);
            return;
        }

        if (!IsPreviewRunning)
        {
            await StartPreviewAsync();
            if (!IsPreviewRunning) return; // preview failed; error already reported
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
                OutputDirectory, SelectedRecordingProfile, SelectedVideoDevice?.Name);
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

    [RelayCommand]
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
        }
    }

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
        NotifyTransportCanExecuteChanged();
    }

    partial void OnDisableTransportDuringRecordingChanged(bool value)
        => NotifyTransportCanExecuteChanged();

    private void UpdateAudioMeters()
    {
        if (!_audioCapture.IsCapturing)
        {
            AudioLevelLeft = 0;
            AudioLevelRight = 0;
            return;
        }

        // PeakLevels consumes held peaks since the last tick; release softens the fall.
        const double release = 0.75;
        var peaks = _audioCapture.PeakLevels;
        var left = peaks.Count > 0 ? peaks[0] : 0;
        var right = peaks.Count > 1 ? peaks[1] : left;
        AudioLevelLeft = Math.Max(left, AudioLevelLeft * release);
        AudioLevelRight = Math.Max(right, AudioLevelRight * release);
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
        RunOnUi(() =>
        {
            CtlText = Timecode.FormatCtlDisplay(time.Ctl, Ctl24HourWrap);
            LtcText = time.Ltc?.ToString() ?? "--:--:--:--";
            VitcText = time.Vitc?.ToString() ?? "--:--:--:--";
            UserBitsText = time.LtcUserBits?.ToString()
                ?? time.VitcUserBits?.ToString()
                ?? "-- -- -- --";
        });
    }

    private void OnRecordingStatusChanged(object? sender, RecordingStatus status)
    {
        RunOnUi(() =>
        {
            RecordingStatusText = status.State switch
            {
                RecordingState.Recording =>
                    $"Recording {status.Elapsed:hh\\:mm\\:ss} ({status.VideoFramesWritten} frames)",
                RecordingState.Faulted => $"Error: {status.Error}",
                _ => status.State.ToString(),
            };
            IsRecording = status.State is RecordingState.Recording or RecordingState.Starting;
            StatusBarRecordingText = status.State switch
            {
                RecordingState.Recording => "● REC",
                RecordingState.Starting => "Starting…",
                RecordingState.Stopping => "Stopping…",
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

    private void RefreshStatusBarCodec()
    {
        StatusBarCodecText = SelectedRecordingProfile is { } profile
            ? $"{profile.VideoCodec} / {profile.AudioCodec} ({profile.Container})"
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