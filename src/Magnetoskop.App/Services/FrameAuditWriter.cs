using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Magnetoskop.App.Services;

/// <summary>
/// One committed video-frame association written to the recording's JSONL audit.
/// Timecode values are deliberately represented as text because the audit is an
/// interchange artifact and must retain source/uncertainty information alongside it.
/// </summary>
public sealed record FrameAuditRecord
{
    public required long RecordingFrameNumber { get; init; }
    public required long SourceFrameNumber { get; init; }
    public required long CaptureTimestamp100Ns { get; init; }
    public required double CaptureOffsetMilliseconds { get; init; }
    public string? ObservedTimecode { get; init; }
    public string? EstimatedTimecode { get; init; }
    public string? TimecodeSource { get; init; }
    public double? ObservationAgeMilliseconds { get; init; }
    public string TimecodeFreshness { get; init; } = "Unavailable";
    public bool IsEstimated { get; init; }
    public bool ServoLocked { get; init; }
    public bool ServoStateFresh { get; init; }
    public bool TapeReverse { get; init; }
    public string? TransportState { get; init; }
}

/// <summary>
/// Non-dropping bounded writer for per-frame JSONL audit data. Producers never
/// block the FFmpeg pump: a full queue is reported as an explicit integrity fault.
/// </summary>
public sealed class FrameAuditWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Channel<FrameAuditRecord> _channel;
    private readonly StreamWriter _writer;
    private readonly Task _pump;
    private readonly TaskCompletionSource<bool> _overflowed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _faulted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completed;
    private long _accepted;
    private long _written;
    private long _rejected;

    public FrameAuditWriter(string path, int capacity = 250)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        Path = fullPath;

        _channel = Channel.CreateBounded<FrameAuditRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024,
            leaveOpen: false);
        _pump = Task.Run(PumpAsync, CancellationToken.None);
    }

    public string Path { get; }
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Written => Interlocked.Read(ref _written);
    public long Rejected => Interlocked.Read(ref _rejected);
    public Task Overflowed => _overflowed.Task;
    public Task<Exception> Faulted => _faulted.Task;
    public Task Completion => _pump;

    public bool TryWrite(FrameAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Volatile.Read(ref _completed) != 0 || !_channel.Writer.TryWrite(record))
        {
            Interlocked.Increment(ref _rejected);
            _overflowed.TrySetResult(true);
            return false;
        }

        Interlocked.Increment(ref _accepted);
        return true;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }

        await _pump.WaitAsync(cancellationToken);
    }

    private async Task PumpAsync()
    {
        var flushClock = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                while (_channel.Reader.TryRead(out var record))
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions));
                    Interlocked.Increment(ref _written);

                    // A continuously non-empty queue must not postpone durable
                    // evidence indefinitely. Check the interval while draining as
                    // well as while idle so every sustained recording is flushed at
                    // least once per second.
                    if (flushClock.Elapsed >= TimeSpan.FromSeconds(1))
                    {
                        await _writer.FlushAsync();
                        flushClock.Restart();
                    }
                }

                if (_channel.Reader.Completion.IsCompleted)
                    break;

                var untilFlush = TimeSpan.FromSeconds(1) - flushClock.Elapsed;
                if (untilFlush <= TimeSpan.Zero)
                {
                    await _writer.FlushAsync();
                    flushClock.Restart();
                    continue;
                }

                var readable = _channel.Reader.WaitToReadAsync().AsTask();
                var flushDue = Task.Delay(untilFlush);
                var completed = await Task.WhenAny(readable, flushDue);
                if (ReferenceEquals(completed, flushDue))
                {
                    await _writer.FlushAsync();
                    flushClock.Restart();
                }
                else if (!await readable)
                {
                    break;
                }
            }

            await _writer.FlushAsync();
        }
        catch (Exception ex)
        {
            _faulted.TrySetResult(ex);
            throw;
        }
        finally
        {
            await _writer.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CompleteAsync();
        }
        catch (Exception)
        {
            // The original failure remains observable through Completion/Faulted.
        }
    }
}
