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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultDirectory));
            })
            .ConfigureServices(services =>
            {
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
        window.Show();
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

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // Dispose view model resources (stops capture, disconnects VTR) before host teardown.
            var vm = _host.Services.GetRequiredService<MainViewModel>();
            await vm.ShutdownAsync();

            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}