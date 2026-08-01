using System.Threading.Channels;
using Magnetoskop.Capture.Audio;
using Magnetoskop.Capture.Video;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Magnetoskop.Simulation;

namespace Magnetoskop.App.Services;

/// <summary>
/// Exposes both real (OpenCV) and simulated video devices in one service.
/// The device Id prefix "sim:" routes to the simulator.
/// </summary>
public sealed class CompositeVideoCaptureService : IVideoCaptureService
{
    private readonly OpenCvVideoCaptureService _real;
    private readonly SimulatedVideoCaptureService _simulated;
    private IVideoCaptureService? _active;

    public CompositeVideoCaptureService(OpenCvVideoCaptureService real, SimulatedVideoCaptureService simulated)
    {
        _real = real;
        _simulated = simulated;
    }

    public bool IsCapturing => _active?.IsCapturing ?? false;

    public VideoFormat? CurrentFormat => _active?.CurrentFormat;

    public async Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    {
        var real = await _real.EnumerateDevicesAsync(cancellationToken);
        var simulated = await _simulated.EnumerateDevicesAsync(cancellationToken);
        // Real devices first; simulated source is the fallback/default when no hardware exists.
        var all = new List<CaptureDeviceInfo>(real.Count + simulated.Count);
        all.AddRange(real.Select(d => d with { IsDefault = real.Count > 0 && d == real[0] }));
        all.AddRange(simulated.Select(d => d with { IsDefault = real.Count == 0 && d.IsDefault }));
        return all;
    }

    public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
    {
        _active = device.Id.StartsWith("sim:", StringComparison.Ordinal) ? _simulated : _real;
        return _active.StartAsync(device, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _active?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    public ChannelReader<VideoFrame> Subscribe(int capacity = 4)
        => (_active ?? _simulated).Subscribe(capacity);

    public async ValueTask DisposeAsync()
    {
        await _real.DisposeAsync();
        await _simulated.DisposeAsync();
    }
}

/// <summary>Same composition for audio: NAudio devices + the simulated tone source.</summary>
public sealed class CompositeAudioCaptureService : IAudioCaptureService
{
    private readonly NAudioCaptureService _real;
    private readonly SimulatedAudioCaptureService _simulated;
    private IAudioCaptureService? _active;

    public CompositeAudioCaptureService(NAudioCaptureService real, SimulatedAudioCaptureService simulated)
    {
        _real = real;
        _simulated = simulated;
    }

    public bool IsCapturing => _active?.IsCapturing ?? false;

    public AudioFormat? CurrentFormat => _active?.CurrentFormat;

    public IReadOnlyList<float> PeakLevels => (_active ?? _real).PeakLevels;

    public async Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    {
        var real = await _real.EnumerateDevicesAsync(cancellationToken);
        var simulated = await _simulated.EnumerateDevicesAsync(cancellationToken);
        var all = new List<CaptureDeviceInfo>(real.Count + simulated.Count);
        all.AddRange(real);
        all.AddRange(simulated.Select(d => d with { IsDefault = real.Count == 0 && d.IsDefault }));
        return all;
    }

    public async Task<CaptureDeviceInfo?> FindMatchingDeviceAsync(
        CaptureDeviceInfo videoDevice, CancellationToken cancellationToken = default)
    {
        // Simulated video pairs with simulated audio; real video with the best real match.
        if (videoDevice.Id.StartsWith("sim:", StringComparison.Ordinal))
        {
            return await _simulated.FindMatchingDeviceAsync(videoDevice, cancellationToken);
        }
        return await _real.FindMatchingDeviceAsync(videoDevice, cancellationToken);
    }

    public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
    {
        _active = device.Id.StartsWith("sim:", StringComparison.Ordinal) ? _simulated : _real;
        return _active.StartAsync(device, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _active?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    public ChannelReader<AudioBuffer> Subscribe(int capacity = 16)
        => (_active ?? _simulated).Subscribe(capacity);

    public async ValueTask DisposeAsync()
    {
        await _real.DisposeAsync();
        await _simulated.DisposeAsync();
    }
}