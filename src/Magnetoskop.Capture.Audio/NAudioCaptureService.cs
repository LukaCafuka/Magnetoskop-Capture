using System.Threading.Channels;
using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Magnetoskop.Capture.Audio;

/// <summary>
/// Audio capture over NAudio WASAPI. Converts the endpoint's mix format to
/// 16-bit PCM, publishes timestamped buffers to bounded subscriber channels,
/// and maintains per-channel peak levels for UI metering.
/// </summary>
public sealed class NAudioCaptureService : IAudioCaptureService
{
    private readonly ILogger<NAudioCaptureService> _logger;
    private readonly List<Channel<AudioBuffer>> _subscribers = new();
    private readonly object _gate = new();

    private WasapiCapture? _capture;
    private AudioFormat? _format;
    private float[] _peaks = new float[2];
    private long _samplesDelivered;

    public NAudioCaptureService(ILogger<NAudioCaptureService> logger)
    {
        _logger = logger;
    }

    public bool IsCapturing { get; private set; }

    public AudioFormat? CurrentFormat => _format;

    public IReadOnlyList<float> PeakLevels => _peaks;

    public Task<IReadOnlyList<CaptureDeviceInfo>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<CaptureDeviceInfo>>(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (Exception)
            {
                // no default capture endpoint present
            }

            var devices = new List<CaptureDeviceInfo>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new CaptureDeviceInfo
                    {
                        Id = device.ID,
                        Name = device.FriendlyName,
                        IsDefault = device.ID == defaultId,
                    });
                }
            }
            return devices;
        }, cancellationToken);
    }

    public async Task<CaptureDeviceInfo?> FindMatchingDeviceAsync(
        CaptureDeviceInfo videoDevice, CancellationToken cancellationToken = default)
    {
        var devices = await EnumerateDevicesAsync(cancellationToken);
        if (devices.Count == 0) return null;

        // Score by shared word tokens between the video and audio device names
        // (capture cards typically expose e.g. "XYZ Video Capture" + "XYZ Audio Capture").
        static HashSet<string> Tokens(string name) => name
            .Split(new[] { ' ', '-', '_', '(', ')', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();

        var videoTokens = Tokens(videoDevice.Name);
        var best = devices
            .Select(d => (Device: d, Score: Tokens(d.Name).Intersect(videoTokens).Count()))
            .OrderByDescending(x => x.Score)
            .First();

        return best.Score > 0
            ? best.Device
            : devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
    }

    public Task StartAsync(CaptureDeviceInfo device, CancellationToken cancellationToken = default)
    {
        if (IsCapturing)
        {
            throw new InvalidOperationException("Audio capture is already running.");
        }

        return Task.Run(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            var mmDevice = enumerator.GetDevice(device.Id)
                ?? throw new IOException($"Audio device '{device.Name}' not found.");

            var capture = new WasapiCapture(mmDevice)
            {
                // Request 16-bit PCM at the device's native rate/channels.
                WaveFormat = new WaveFormat(mmDevice.AudioClient.MixFormat.SampleRate,
                    16, Math.Min(2, mmDevice.AudioClient.MixFormat.Channels)),
            };

            var format = new AudioFormat
            {
                SampleRate = capture.WaveFormat.SampleRate,
                Channels = capture.WaveFormat.Channels,
                BitsPerSample = capture.WaveFormat.BitsPerSample,
            };

            _peaks = new float[format.Channels];
            _samplesDelivered = 0;

            capture.DataAvailable += (_, e) => OnDataAvailable(e, format);
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    _logger.LogError(e.Exception, "Audio capture stopped due to an error");
                }
                IsCapturing = false;
            };

            capture.StartRecording();
            _capture = capture;
            _format = format;
            IsCapturing = true;

            _logger.LogInformation("Audio capture started: {Device} {Rate} Hz, {Channels} ch, {Bits} bit",
                device.Name, format.SampleRate, format.Channels, format.BitsPerSample);
        }, cancellationToken);
    }

    private void OnDataAvailable(WaveInEventArgs e, AudioFormat format)
    {
        if (e.BytesRecorded == 0) return;

        var data = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);

        UpdatePeaks(data, format);

        var timestamp = TimeSpan.FromSeconds(
            (double)_samplesDelivered / format.SampleRate);
        _samplesDelivered += e.BytesRecorded / (format.Channels * (format.BitsPerSample / 8));

        var buffer = new AudioBuffer
        {
            Data = data,
            Length = data.Length,
            Format = format,
            Timestamp = timestamp,
        };

        lock (_gate)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryWrite(buffer);
            }
        }
    }

    private void UpdatePeaks(byte[] data, AudioFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var frameBytes = bytesPerSample * format.Channels;
        var peaks = new float[format.Channels];

        for (var offset = 0; offset + frameBytes <= data.Length; offset += frameBytes)
        {
            for (var ch = 0; ch < format.Channels; ch++)
            {
                var sample = BitConverter.ToInt16(data, offset + ch * bytesPerSample);
                var level = Math.Abs(sample / (float)short.MaxValue);
                if (level > peaks[ch]) peaks[ch] = level;
            }
        }

        _peaks = peaks;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_capture is null) return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                _capture.StopRecording();
            }
            finally
            {
                _capture.Dispose();
                _capture = null;
                _format = null;
                Array.Clear(_peaks);
                IsCapturing = false;
                _logger.LogInformation("Audio capture stopped");
            }
        }, cancellationToken);
    }

    public ChannelReader<AudioBuffer> Subscribe(int capacity = 16)
    {
        var channel = Channel.CreateBounded<AudioBuffer>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false, // WASAPI callback thread
        });
        lock (_gate)
        {
            _subscribers.Add(channel);
        }
        return channel.Reader;
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