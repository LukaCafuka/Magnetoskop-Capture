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

    public List<TransportCommand> SentCommands { get; } = new();

    /// <summary>Invoked for every transport command; lets tests mutate state.</summary>
    public Action<TransportCommand>? OnTransportCommand { get; set; }

    public event EventHandler<VtrStatus>? StatusChanged;
    public event EventHandler<TimeInformation>? TimeChanged;

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

    public void RaiseStatus() => StatusChanged?.Invoke(this, CurrentStatus);
    public void RaiseTime() => TimeChanged?.Invoke(this, CurrentTime);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>IVideoCaptureService stand-in; never produces frames.</summary>
public sealed class FakeVideoCaptureService : IVideoCaptureService
{
    public bool IsCapturing { get; set; }
    public VideoFormat? CurrentFormat { get; set; }

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

    public ChannelReader<VideoFrame> Subscribe(int capacity = 4)
        => Channel.CreateBounded<VideoFrame>(capacity).Reader;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>IAudioCaptureService stand-in; never produces buffers.</summary>
public sealed class FakeAudioCaptureService : IAudioCaptureService
{
    public bool IsCapturing { get; set; }
    public AudioFormat? CurrentFormat { get; set; }
    public IReadOnlyList<float> PeakLevels { get; } = new float[] { 0, 0 };

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

    public ChannelReader<AudioBuffer> Subscribe(int capacity = 16)
        => Channel.CreateBounded<AudioBuffer>(capacity).Reader;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
