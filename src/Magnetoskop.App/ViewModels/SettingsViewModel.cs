using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magnetoskop.App.Services;

namespace Magnetoskop.App.ViewModels;

/// <summary>Draft settings for the Settings window (OK applies, Cancel discards).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(
        bool debugLoggingEnabled,
        bool showLogPanel,
        bool disableTransportDuringRecording,
        bool mediaKeysControlTransport,
        bool previewYadif2xEnabled,
        bool ctl24HourWrap)
    {
        DebugLoggingEnabled = debugLoggingEnabled;
        ShowLogPanel = showLogPanel;
        DisableTransportDuringRecording = disableTransportDuringRecording;
        MediaKeysControlTransport = mediaKeysControlTransport;
        PreviewYadif2xEnabled = previewYadif2xEnabled;
        Ctl24HourWrap = ctl24HourWrap;
        DebugLogFolderHint = DebugSessionFileLoggerProvider.LogDirectory;
    }

    [ObservableProperty]
    private bool _debugLoggingEnabled;

    [ObservableProperty]
    private bool _showLogPanel;

    [ObservableProperty]
    private bool _disableTransportDuringRecording = true;

    [ObservableProperty]
    private bool _mediaKeysControlTransport = true;

    [ObservableProperty]
    private bool _previewYadif2xEnabled;

    [ObservableProperty]
    private bool _ctl24HourWrap;

    /// <summary>Shown under the debug-logging checkbox so the user knows where files go.</summary>
    public string DebugLogFolderHint { get; }

    public bool DialogAccepted { get; private set; }

    public event EventHandler? RequestClose;

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
}
