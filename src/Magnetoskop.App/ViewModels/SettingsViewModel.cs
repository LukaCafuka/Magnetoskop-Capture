using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magnetoskop.App.Services;
using Magnetoskop.Core.Models;

namespace Magnetoskop.App.ViewModels;

/// <summary>Draft settings for the Settings window (OK applies, Cancel discards).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(
        bool debugLoggingEnabled,
        bool showLogPanel,
        bool disableTransportDuringRecording,
        bool previewYadif2xEnabled,
        bool ctl24HourWrap,
        IEnumerable<VtrDeviceProfile> vtrProfiles,
        string? selectedVtrProfileId)
    {
        DebugLoggingEnabled = debugLoggingEnabled;
        ShowLogPanel = showLogPanel;
        DisableTransportDuringRecording = disableTransportDuringRecording;
        PreviewYadif2xEnabled = previewYadif2xEnabled;
        Ctl24HourWrap = ctl24HourWrap;
        DebugLogFolderHint = DebugSessionFileLoggerProvider.LogDirectory;

        VtrProfiles = new ObservableCollection<VtrDeviceProfile>(vtrProfiles);
        SelectedVtrProfile = VtrProfiles.FirstOrDefault(p => p.Id == selectedVtrProfileId)
            ?? VtrProfiles.FirstOrDefault(p => p.Id == VtrDeviceProfile.Generic.Id)
            ?? VtrDeviceProfile.Generic;
    }

    [ObservableProperty]
    private bool _debugLoggingEnabled;

    [ObservableProperty]
    private bool _showLogPanel;

    [ObservableProperty]
    private bool _disableTransportDuringRecording = true;

    [ObservableProperty]
    private bool _previewYadif2xEnabled;

    [ObservableProperty]
    private bool _ctl24HourWrap;

    [ObservableProperty]
    private VtrDeviceProfile _selectedVtrProfile = VtrDeviceProfile.Generic;

    public ObservableCollection<VtrDeviceProfile> VtrProfiles { get; }

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
