using System.Threading.Channels;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;

namespace Magnetoskop.App.Tests;

/// <summary>Deterministic IVtrController stand-in with directly settable state.</summary>
public sealed class FakeVtrController : IVtrController
{
    public string DeviceDescription { get; set; } = "Fake VTR";
    public bool IsConnected { get; set; }
    public VtrStatus CurrentStatus { get; set; } = new();
    public TimeInformation CurrentTime { get; set; } = new();
    public VtrLinkHealth LinkHealth { get; set; } = new();

    public List<TransportCommand> SentCommands { get; } = new();

    /// <summary>Invoked for every transport command; lets tests mutate state.</summary>
    public Action<TransportCommand>? OnTransportCommand { get; set; }

    public event EventHandler<VtrStatus>? StatusChanged;
    public event EventHandler<TimeInformation>? TimeChanged;
    public event EventHandler<VtrLinkHealth>? LinkHealthChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task SendTransportCommandAsync(TransportCommand command, CancellationToken cancellationToken = default)
    {
        SentCommands.Add(command);
        OnTransportCommand?.Invoke(command);
        return Task.CompletedTask;
    }

    public Task SendVariableSpeedAsync(
        VariableSpeedMode mode,
        bool forward,
        byte speed,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public List<Timecode> CueUpTargets { get; } = new();
    public List<CueUpTimerMode> CueUpModes { get; } = new();

    public Task CueUpAsync(
        Timecode timecode,
        CueUpTimerMode timerMode = CueUpTimerMode.TimeCode,
        CancellationToken cancellationToken = default)
    {
        CueUpTargets.Add(timecode);
        CueUpModes.Add(timerMode);
        SentCommands.Add(TransportCommand.CueUp);
        return Task.CompletedTask;
    }

    public void RaiseStatus() => StatusChanged?.Invoke(this, CurrentStatus);
    public void RaiseTime() => TimeChanged?.Invoke(this, CurrentTime);
    public void RaiseLinkHealth() => LinkHealthChanged?.Invoke(this, LinkHealth);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>IVideoCaptureService stand-in; never produces frames.</summary>
public class FakeVideoCaptureService : IVideoCaptureService
{
    public bool IsCapturing { get; set; }
    public VideoFormat? CurrentFormat { get; set; }
    public CaptureHealth Health { get; set; } = new()
    {
        State = CaptureHealthState.Stopped,
        Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
    };

    public event EventHandler<CaptureHealthEventArgs>? HealthChanged;

    public Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CaptureDeviceInfo>>(Array.Empty<CaptureDeviceInfo>());

    public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
    {
        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public CaptureSubscription<VideoFrame> Subscribe(int capacity = 4)
        => Subscribe(CaptureSubscriptionOptions.Preview(capacity));

    public CaptureSubscription<VideoFrame> Subscribe(CaptureSubscriptionOptions options)
        => new(options, singleWriter: true);

    public void RaiseHealth() => HealthChanged?.Invoke(this, new CaptureHealthEventArgs(Health));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Configurable video stand-in for coordinator format-gate tests.</summary>
public sealed class FakeConfigurableVideoCaptureService :
    FakeVideoCaptureService,
    IConfigurableVideoCaptureService
{
    public VideoInputConfiguration InputConfiguration { get; set; }
        = VideoInputConfiguration.SimulatedPalTff;

    public VideoInputFormatStatus FormatStatus { get; set; }
        = VideoInputFormatStatus.FromReadback(
            VideoInputConfiguration.SimulatedPalTff,
            new VideoFormat
            {
                Width = 720,
                Height = 576,
                FrameRate = 25,
                Interlaced = true,
                TopFieldFirst = true,
            },
            scanReadbackAvailable: true);
}

/// <summary>IAudioCaptureService stand-in; never produces buffers.</summary>
public sealed class FakeAudioCaptureService : IAudioCaptureService
{
    public bool IsCapturing { get; set; }
    public AudioFormat? CurrentFormat { get; set; }
    public IReadOnlyList<float> PeakLevels { get; private set; } = new float[] { 0, 0 };
    public CaptureHealth Health { get; set; } = new()
    {
        State = CaptureHealthState.Stopped,
        Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
    };

    public event EventHandler<CaptureHealthEventArgs>? HealthChanged;

    public Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CaptureDeviceInfo>>(Array.Empty<CaptureDeviceInfo>());

    public Task<CaptureDeviceInfo?> FindMatchingDeviceAsync(CaptureDeviceInfo videoDevice, CancellationToken cancellationToken = default)
        => Task.FromResult<CaptureDeviceInfo?>(null);

    public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
    {
        IsCapturing = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public CaptureSubscription<AudioBuffer> Subscribe(int capacity = 16)
        => Subscribe(CaptureSubscriptionOptions.Monitor(capacity));

    public CaptureSubscription<AudioBuffer> Subscribe(CaptureSubscriptionOptions options)
        => new(options, singleWriter: true);

    public void RaiseHealth() => HealthChanged?.Invoke(this, new CaptureHealthEventArgs(Health));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
