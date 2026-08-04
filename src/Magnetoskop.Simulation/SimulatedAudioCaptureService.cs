using System.Threading.Channels;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Simulation;

/// <summary>Generates a stereo 1 kHz sine tone so the audio pipeline can run without hardware.</summary>
public sealed class SimulatedAudioCaptureService : IAudioCaptureService
{
    private static readonly AudioFormat Format = new()
    {
        SampleRate = 48000,
        Channels = 2,
        BitsPerSample = 16,
    };

    private readonly ILogger<SimulatedAudioCaptureService> _logger;
    private readonly List<Channel<AudioBuffer>> _subscribers = new();
    private readonly object _gate = new();
    private readonly float[] _peaks = new float[2];

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SimulatedAudioCaptureService(ILogger<SimulatedAudioCaptureService> logger)
    {
        _logger = logger;
    }

    public bool IsCapturing { get; private set; }

    public AudioFormat? CurrentFormat => IsCapturing ? Format : null;

    public IReadOnlyList<float> PeakLevels
    {
        get
        {
            lock (_gate)
            {
                var copy = (float[])_peaks.Clone();
                Array.Clear(_peaks);
                return copy;
            }
        }
    }

    public Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CaptureDeviceInfo> devices = new[]
        {
            new CaptureDeviceInfo { Id = "sim:tone", Name = "Simulated audio source (1 kHz tone)", IsDefault = true },
        };
        return Task.FromResult(devices);
    }

    public async Task<CaptureDeviceInfo?> FindMatchingDeviceAsync(CaptureDeviceInfo videoDevice, CancellationToken cancellationToken = default)
    {
        var devices = await EnumerateDevicesAsync(cancellationToken);
        return devices.FirstOrDefault();
    }

    public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
    {
        if (IsCapturing) return Task.CompletedTask;

        IsCapturing = true;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => GenerateAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Simulated audio capture started ({Device})", device.Name);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCapturing) return;

        IsCapturing = false;
        if (_cts is not null) await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        lock (_gate)
        {
            Array.Clear(_peaks);
        }
        _logger.LogInformation("Simulated audio capture stopped");
    }

    public ChannelReader<AudioBuffer> Subscribe(int capacity = 16)
    {
        var channel = Channel.CreateBounded<AudioBuffer>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
        });
        lock (_gate)
        {
            _subscribers.Add(channel);
        }
        return channel.Reader;
    }

    private async Task GenerateAsync(CancellationToken ct)
    {
        const double frequencyHz = 1000.0;
        const double amplitude = 0.5;
        const int blockMs = 20;
        var samplesPerBlock = Format.SampleRate * blockMs / 1000;
        var bytesPerBlock = samplesPerBlock * Format.Channels * (Format.BitsPerSample / 8);

        long sampleIndex = 0;
        var start = TimeSpan.FromMilliseconds(Environment.TickCount64);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(blockMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var data = new byte[bytesPerBlock];
            float peak = 0;

            for (var i = 0; i < samplesPerBlock; i++)
            {
                var t = (double)(sampleIndex + i) / Format.SampleRate;
                var value = amplitude * Math.Sin(2 * Math.PI * frequencyHz * t);
                var sample = (short)(value * short.MaxValue);
                peak = Math.Max(peak, Math.Abs((float)value));

                var offset = i * Format.Channels * 2;
                // Left + right (identical).
                data[offset] = (byte)(sample & 0xFF);
                data[offset + 1] = (byte)(sample >> 8 & 0xFF);
                data[offset + 2] = data[offset];
                data[offset + 3] = data[offset + 1];
            }

            lock (_gate)
            {
                if (peak > _peaks[0]) _peaks[0] = peak;
                if (peak > _peaks[1]) _peaks[1] = peak;
            }

            var buffer = new AudioBuffer
            {
                Data = data,
                Length = data.Length,
                Format = Format,
                Timestamp = TimeSpan.FromSeconds((double)sampleIndex / Format.SampleRate) ,
            };
            sampleIndex += samplesPerBlock;
            _ = start; // start reserved for wallclock alignment in later phases

            lock (_gate)
            {
                foreach (var sub in _subscribers)
                {
                    sub.Writer.TryWrite(buffer);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lock (_gate)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryComplete();
            }
            _subscribers.Clear();
        }
    }
}