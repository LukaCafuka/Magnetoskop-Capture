using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Magnetoskop.Capture.Audio;

/// <summary>
/// Minimal shared-mode WASAPI packet reader. Unlike NAudio's DataAvailable event,
/// this uses AudioCaptureClient.GetBuffer's extended overload so device position,
/// normalized QPC position, and discontinuity flags are preserved.
/// </summary>
internal sealed class WasapiPacketCapture : IDisposable
{
    private const long BufferDuration100ns = 1_000_000; // 100 ms

    private readonly object _gate = new();
    private readonly AudioClient _audioClient;
    private readonly AudioCaptureClient _captureClient;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private bool _disposed;

    public WasapiPacketCapture(MMDevice device, WaveFormat waveFormat)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(waveFormat);

        WaveFormat = waveFormat;
        _audioClient = device.AudioClient;

        // Shared-mode conversion matches the previous WasapiCapture behavior while
        // keeping a predictable 16-bit PCM contract for the rest of the application.
        var flags = AudioClientStreamFlags.AutoConvertPcm
                    | AudioClientStreamFlags.SrcDefaultQuality;
        _audioClient.Initialize(
            AudioClientShareMode.Shared,
            flags,
            BufferDuration100ns,
            0,
            waveFormat,
            Guid.Empty);
        _captureClient = _audioClient.AudioCaptureClient;
    }

    public WaveFormat WaveFormat { get; }

    public event EventHandler<WasapiPacketEventArgs>? PacketAvailable;
    public event EventHandler<WasapiCaptureStoppedEventArgs>? CaptureStopped;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) throw new InvalidOperationException("WASAPI capture is already running.");

            _cts = new CancellationTokenSource();
            _audioClient.Start();
            _thread = new Thread(() => CaptureLoop(_cts.Token))
            {
                Name = "WasapiPacketCapture",
                IsBackground = true,
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            _cts?.Cancel();
            thread = _thread;
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(3));
        }

        lock (_gate)
        {
            if (_thread is not null)
            {
                try { _audioClient.Stop(); } catch (Exception) { }
            }
            _thread = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packetRead = false;
                while (!cancellationToken.IsCancellationRequested
                       && _captureClient.GetNextPacketSize() > 0)
                {
                    packetRead = true;
                    ReadPacket();
                }

                if (!packetRead)
                {
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(3));
                }
            }
        }
        catch (Exception ex)
        {
            terminalError = ex;
        }
        finally
        {
            try { _audioClient.Stop(); } catch (Exception) { }
            CaptureStopped?.Invoke(this, new WasapiCaptureStoppedEventArgs(terminalError));
        }
    }

    private void ReadPacket()
    {
        var pointer = _captureClient.GetBuffer(
            out var sampleCount,
            out var flags,
            out var devicePosition,
            out var qpcPosition100ns);

        try
        {
            var byteCount = checked(sampleCount * WaveFormat.BlockAlign);
            var data = new byte[byteCount];
            var silent = (flags & AudioClientBufferFlags.Silent) != 0;
            if (!silent && byteCount > 0)
            {
                Marshal.Copy(pointer, data, 0, byteCount);
            }

            PacketAvailable?.Invoke(this, new WasapiPacketEventArgs(
                data,
                sampleCount,
                flags,
                devicePosition,
                qpcPosition100ns));
        }
        finally
        {
            _captureClient.ReleaseBuffer(sampleCount);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
        _captureClient.Dispose();
        _audioClient.Dispose();
    }
}

internal sealed class WasapiPacketEventArgs(
    byte[] data,
    int sampleCount,
    AudioClientBufferFlags flags,
    long devicePosition,
    long qpcPosition100ns) : EventArgs
{
    public byte[] Data { get; } = data;
    public int SampleCount { get; } = sampleCount;
    public AudioClientBufferFlags Flags { get; } = flags;
    public long DevicePosition { get; } = devicePosition;
    public long QpcPosition100ns { get; } = qpcPosition100ns;
}

internal sealed class WasapiCaptureStoppedEventArgs(Exception? exception) : EventArgs
{
    public Exception? Exception { get; } = exception;
}
