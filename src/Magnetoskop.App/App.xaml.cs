using System.ComponentModel;
using System.Windows;
using Magnetoskop.App.Services;
using Magnetoskop.App.ViewModels;
using Magnetoskop.App.Views;
using Magnetoskop.Capture.Audio;
using Magnetoskop.Capture.Video;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Recording;
using Magnetoskop.Serial;
using Magnetoskop.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.App;

public partial class App : Application
{
    private IHost? _host;
    private int _shutdownStarted;
    private bool _shutdownCompleted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var debugLogger = new DebugSessionFileLoggerProvider();

        // Load settings early so debug logging can be enabled before the host starts.
        var bootstrapSettings = new SettingsService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsService>.Instance);
        var saved = bootstrapSettings.Load();
        debugLogger.SetEnabled(saved.DebugLoggingEnabled);

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultDirectory));
                logging.AddProvider(debugLogger);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(debugLogger);

                // VTR: runtime-switchable between the simulator and Sony 9-pin on a COM port.
                services.AddSingleton<SimulatedVtr>();
                services.AddSingleton<ISerialPortEnumerator, SerialPortEnumerator>();
                services.AddSingleton<VtrConnectionService>();
                services.AddSingleton<IVtrController>(sp => sp.GetRequiredService<VtrConnectionService>());

                // Capture: real devices (OpenCV / NAudio WASAPI) + simulated fallbacks.
                services.AddSingleton<OpenCvVideoCaptureService>();
                services.AddSingleton<SimulatedVideoCaptureService>();
                services.AddSingleton<IVideoCaptureService, CompositeVideoCaptureService>();
                services.AddSingleton<NAudioCaptureService>();
                services.AddSingleton<SimulatedAudioCaptureService>();
                services.AddSingleton<IAudioCaptureService, CompositeAudioCaptureService>();
                services.AddSingleton<AudioMonitorService>();

                // Recording via external FFmpeg process (FFV1/H.264/ProRes).
                services.AddSingleton<IRecordingService, FfmpegRecordingService>();

                // Workflow coordination + persisted user settings.
                services.AddSingleton<CaptureSessionCoordinator>();
                services.AddSingleton<SettingsService>();

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        // Global safety nets: log and surface unhandled errors without tearing the
        // process down (an active recording must survive a UI exception).
        DispatcherUnhandledException += (_, args) =>
        {
            ReportUnhandled("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportUnhandled("Unobserved background exception", args.Exception);
            args.SetObserved();
        };

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Closing += OnMainWindowClosing;
        window.Show();
    }

    /// <summary>
    /// WPF's OnExit callback is synchronous from the application's perspective;
    /// making that override async-void can let the process disappear while FFmpeg
    /// or a sidecar is still finalizing. Cancel the first close, await the complete
    /// shutdown pipeline, then allow a second close to finish normally.
    /// </summary>
    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted) return;

        e.Cancel = true;
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;

        try
        {
            await ShutdownHostAsync();
        }
        catch (Exception ex)
        {
            ReportUnhandled("Application shutdown failed", ex);
        }
        finally
        {
            _shutdownCompleted = true;
            if (sender is MainWindow window)
            {
                window.Closing -= OnMainWindowClosing;
                window.Close();
            }
        }
    }

    private async Task ShutdownHostAsync()
    {
        var host = _host;
        if (host is null) return;

        // Finalize recording/audit/summary before capture devices and DI-owned
        // services are torn down.
        var vm = host.Services.GetRequiredService<MainViewModel>();
        try
        {
            await vm.ShutdownAsync();
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _host = null;
            if (host is IAsyncDisposable asyncHost)
            {
                // Capture/recording services own asynchronous pumps and device handles.
                // Disposing the DI container synchronously can reject async-only services.
                await asyncHost.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }
    }

    private void ReportUnhandled(string context, Exception exception)
    {
        try
        {
            var services = _host?.Services;
            services?.GetRequiredService<ILogger<App>>()
                .LogError(exception, "{Context}", context);
            services?.GetRequiredService<MainViewModel>()
                .ReportUnhandledException(context, exception);
        }
        catch (Exception)
        {
            // The safety net itself must never throw.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Normal main-window shutdown has already awaited ShutdownHostAsync in the
        // cancellable Closing phase. Dispose only as a last-resort startup-failure
        // cleanup; never start asynchronous media teardown from OnExit.
        _host?.Dispose();
        _host = null;
        base.OnExit(e);
    }
}
