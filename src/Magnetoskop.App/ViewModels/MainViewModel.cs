using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magnetoskop.App.Services;
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
    private readonly IRecordingService _recorder;
    private readonly CaptureSessionCoordinator _session;
    private readonly SettingsService _settings;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _meterTimer;

    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private WriteableBitmap? _previewBitmap;

    public MainViewModel(
        VtrConnectionService vtr,
        IVideoCaptureService videoCapture,
        IAudioCaptureService audioCapture,
        IRecordingService recorder,
        CaptureSessionCoordinator session,
        SettingsService settings,
        ILogger<MainViewModel> logger)
    {
        _vtr = vtr;
        _videoCapture = videoCapture;
        _audioCapture = audioCapture;
        _recorder = recorder;
        _session = session;
        _settings = settings;
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

        OutputDirectory = saved.OutputDirectory is { Length: > 0 } dir && Directory.Exists(dir)
            ? dir
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        RecordingProfiles = new ObservableCollection<RecordingProfile>(RecordingProfile.Defaults);
        SelectedRecordingProfile = RecordingProfiles.FirstOrDefault(p => p.Id == saved.RecordingProfileId)
            ?? RecordingProfiles.FirstOrDefault();

        VtrConnections = new ObservableCollection<VtrConnectionOption>(_vtr.GetConnectionOptions());
        VtrProfiles = new ObservableCollection<VtrDeviceProfile>(_vtr.GetDeviceProfiles());
        SelectedVtrConnection = VtrConnections.FirstOrDefault(o => o.Id == saved.VtrConnectionId)
            ?? VtrConnections.FirstOrDefault();
        SelectedVtrProfile = VtrProfiles.FirstOrDefault(p => p.Id == saved.VtrProfileId)
            ?? VtrProfiles.FirstOrDefault();
        AudioManuallySelected = saved.AudioManuallySelected;
        AutoPlayOnRecord = saved.AutoPlayOnRecord;

        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _meterTimer.Tick += (_, _) => UpdateAudioMeters();
        _meterTimer.Start();
    }

    // ---- Observable state ------------------------------------------------

    [ObservableProperty]
    private string _deviceDescription = "(not connected)";

    [ObservableProperty]
    private bool _isVtrConnected;

    [ObservableProperty]
    private string _transportStateText = "—";

    [ObservableProperty]
    private string _ctlText = "--:--:--:--";

    [ObservableProperty]
    private string _ltcText = "--:--:--:--";

    [ObservableProperty]
    private string _vitcText = "--:--:--:--";

    [ObservableProperty]
    private string _userBitsText = "-- -- -- --";

    [ObservableProperty]
    private string _statusFlagsText = "";

    /// <summary>Device-type identification result shown in the recorder status panel.</summary>
    [ObservableProperty]
    private string _detectedProfileText = "";

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

    [ObservableProperty]
    private string _outputDirectory;

    [ObservableProperty]
    private RecordingProfile? _selectedRecordingProfile;

    [ObservableProperty]
    private bool _isRecording;

    /// <summary>When true, Record issues Play on the deck and waits for servo lock first.</summary>
    [ObservableProperty]
    private bool _autoPlayOnRecord;

    [ObservableProperty]
    private string _recordingStatusText = "Idle";

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
    public ObservableCollection<RecordingProfile> RecordingProfiles { get; }
    public ObservableCollection<string> LogEntries { get; } = new();

    // ---- Lifecycle ---------------------------------------------------------

    [RelayCommand]
    private async Task InitializeAsync()
    {
        try
        {
            await RefreshDevicesAsync();

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
        }
        catch (Exception ex)
        {
            ReportError("Initialization failed", ex);
        }
    }

    [RelayCommand]
    private async Task ConnectVtrAsync()
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
        var selected = SelectedVtrConnection?.Id;
        VtrConnections.Clear();
        foreach (var option in _vtr.GetConnectionOptions()) VtrConnections.Add(option);
        SelectedVtrConnection = VtrConnections.FirstOrDefault(o => o.Id == selected)
            ?? VtrConnections.FirstOrDefault();
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
        var s = _settings.Current;
        s.OutputDirectory = OutputDirectory;
        s.RecordingProfileId = SelectedRecordingProfile?.Id;
        s.VideoDeviceId = SelectedVideoDevice?.Id;
        s.AudioDeviceId = SelectedAudioDevice?.Id;
        s.AudioManuallySelected = AudioManuallySelected;
        s.VtrConnectionId = SelectedVtrConnection?.Id;
        s.VtrProfileId = SelectedVtrProfile?.Id;
        s.FfmpegPath = Recording.FfmpegLocator.ConfiguredPath;
        s.AutoPlayOnRecord = AutoPlayOnRecord;
        _settings.Save();
    }

    // ---- Device selection ----------------------------------------------------

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        try
        {
            var video = await _videoCapture.EnumerateDevicesAsync();
            var audio = await _audioCapture.EnumerateDevicesAsync();

            VideoDevices.Clear();
            foreach (var d in video) VideoDevices.Add(d);
            AudioDevices.Clear();
            foreach (var d in audio) AudioDevices.Add(d);

            // Prefer the devices remembered from the previous session, then defaults.
            SelectedVideoDevice ??=
                VideoDevices.FirstOrDefault(d => d.Id == _settings.Current.VideoDeviceId)
                ?? VideoDevices.FirstOrDefault(d => d.IsDefault)
                ?? VideoDevices.FirstOrDefault();
            if (AudioManuallySelected)
            {
                SelectedAudioDevice ??= AudioDevices.FirstOrDefault(d => d.Id == _settings.Current.AudioDeviceId);
                if (SelectedAudioDevice is null)
                {
                    // Saved device is gone; fall back to auto-select behavior.
                    AudioManuallySelected = false;
                    await AutoSelectAudioAsync();
                }
            }
            else
            {
                await AutoSelectAudioAsync();
            }
        }
        catch (Exception ex)
        {
            ReportError("Device enumeration failed", ex);
        }
    }

    partial void OnSelectedVideoDeviceChanged(CaptureDeviceInfo? value)
    {
        if (value is not null && !AudioManuallySelected)
        {
            _ = AutoSelectAudioAsync();
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

    [RelayCommand]
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
        }
        catch (Exception ex)
        {
            ReportError("Failed to start preview", ex);
        }
    }

    [RelayCommand]
    private async Task StopPreviewAsync()
    {
        await StopPreviewInternalAsync();
        AppendLog("Preview stopped");
    }

    private async Task StopPreviewInternalAsync()
    {
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

    private async Task PreviewLoopAsync(ChannelReader<VideoFrame> reader, CancellationToken ct)
    {
        await foreach (var frame in reader.ReadAllAsync(ct))
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            await dispatcher.InvokeAsync(() =>
            {
                RenderFrame(frame);
            });
        }
    }

    private void RenderFrame(VideoFrame frame)
    {
        var f = frame.Format;
        if (_previewBitmap is null
            || _previewBitmap.PixelWidth != f.Width
            || _previewBitmap.PixelHeight != f.Height)
        {
            _previewBitmap = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgr24, null);
            PreviewSource = _previewBitmap;
        }

        var stride = f.Width * 3;
        _previewBitmap.WritePixels(
            new Int32Rect(0, 0, f.Width, f.Height), frame.Data, stride, 0);
    }

    // ---- Transport ------------------------------------------------------------

    [RelayCommand] private Task PlayAsync() => SendTransportAsync(TransportCommand.Play);
    [RelayCommand] private Task StopAsync() => SendTransportAsync(TransportCommand.Stop);
    [RelayCommand] private Task FastForwardAsync() => SendTransportAsync(TransportCommand.FastForward);
    [RelayCommand] private Task RewindAsync() => SendTransportAsync(TransportCommand.Rewind);
    [RelayCommand] private Task EjectAsync() => SendTransportAsync(TransportCommand.Eject);

    private async Task SendTransportAsync(TransportCommand command)
    {
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

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (IsRecording || SelectedRecordingProfile is null) return;

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
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            InitialDirectory = OutputDirectory,
        };
        if (dialog.ShowDialog() == true)
        {
            OutputDirectory = dialog.FolderName;
        }
    }

    private void UpdateAudioMeters()
    {
        if (!_audioCapture.IsCapturing)
        {
            AudioLevelLeft = 0;
            AudioLevelRight = 0;
            return;
        }
        var peaks = _audioCapture.PeakLevels;
        AudioLevelLeft = peaks.Count > 0 ? peaks[0] : 0;
        AudioLevelRight = peaks.Count > 1 ? peaks[1] : AudioLevelLeft;
    }

    // ---- Event handlers ---------------------------------------------------------

    private void OnVtrStatusChanged(object? sender, VtrStatus status)
    {
        RunOnUi(() =>
        {
            IsVtrConnected = status.IsConnected;
            TransportStateText = status.Transport.ToString();

            var flags = new List<string>();
            if (status.TapeOut) flags.Add("TAPE OUT");
            if (status.IsLocal) flags.Add("LOCAL");
            if (status.ServoLock) flags.Add("SERVO LOCK");
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
            CtlText = time.Ctl?.ToString() ?? "--:--:--:--";
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
            if (status.State == RecordingState.Faulted && status.Error is not null)
            {
                ReportError(status.Error, null);
            }
        });
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