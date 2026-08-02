using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magnetoskop.App.Services;

namespace Magnetoskop.App.ViewModels;

/// <summary>Draft settings for the Settings window (OK applies, Cancel discards).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(bool debugLoggingEnabled, bool showLogPanel)
    {
        DebugLoggingEnabled = debugLoggingEnabled;
        ShowLogPanel = showLogPanel;
        DebugLogFolderHint = DebugSessionFileLoggerProvider.LogDirectory;
    }

    [ObservableProperty]
    private bool _debugLoggingEnabled;

    [ObservableProperty]
    private bool _showLogPanel;

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
