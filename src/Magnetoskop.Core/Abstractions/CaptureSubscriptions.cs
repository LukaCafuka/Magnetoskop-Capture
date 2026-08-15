using System.Threading.Channels;
using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Abstractions;

/// <summary>Purpose of a capture-stream subscription.</summary>
public enum CaptureConsumerRole
{
    Preview,
    Monitor,
    Recording,
    FrameAudit,
}

/// <summary>Behavior when a bounded capture subscription is full.</summary>
public enum CaptureOverflowPolicy
{
    /// <summary>Keep the newest live item and evict the oldest queued item.</summary>
    DropOldest,

    /// <summary>Preserve every queued item and reject the newly published item.</summary>
    RejectNew,
}

/// <summary>
/// Configuration for an independent capture consumer. Recording is deliberately
/// loss-intolerant: its queue never silently replaces an accepted item.
/// </summary>
public sealed record CaptureSubscriptionOptions
{
    public required CaptureConsumerRole Role { get; init; }
    public required int Capacity { get; init; }

    public CaptureOverflowPolicy OverflowPolicy => Role is CaptureConsumerRole.Recording
        or CaptureConsumerRole.FrameAudit
        ? CaptureOverflowPolicy.RejectNew
        : CaptureOverflowPolicy.DropOldest;

    public static CaptureSubscriptionOptions Preview(int capacity = 4) => new()
    {
        Role = CaptureConsumerRole.Preview,
        Capacity = capacity,
    };

    public static CaptureSubscriptionOptions Monitor(int capacity = 16) => new()
    {
        Role = CaptureConsumerRole.Monitor,
        Capacity = capacity,
    };

    public static CaptureSubscriptionOptions Recording(int capacity) => new()
    {
        Role = CaptureConsumerRole.Recording,
        Capacity = capacity,
    };

    public static CaptureSubscriptionOptions FrameAudit(int capacity) => new()
    {
        Role = CaptureConsumerRole.FrameAudit,
        Capacity = capacity,
    };
}

public enum CaptureOverflowKind
{
    OldestItemDropped,
    NewItemRejected,
}

/// <summary>The first-class signal emitted whenever a bounded subscription overflows.</summary>
public sealed record CaptureOverflow
{
    public required CaptureOverflowKind Kind { get; init; }
    public required CaptureConsumerRole Role { get; init; }
    public required long Timestamp100ns { get; init; }
    public required long OverflowCount { get; init; }
}

/// <summary>Atomic point-in-time counters for one capture subscription.</summary>
public readonly record struct CaptureSubscriptionMetrics(
    long PublishAttempts,
    long Enqueued,
    long Dequeued,
    long DroppedOldest,
    long RejectedNew,
    int QueueDepth,
    int HighWatermark)
{
    public long OverflowCount => DroppedOldest + RejectedNew;
}

/// <summary>
/// Disposable, role-aware view of a bounded capture stream. Implementations expose
/// channel-style asynchronous reads without leaking a producer/writer handle.
/// </summary>
public interface ICaptureSubscription<T> : IAsyncDisposable, IDisposable
{
    CaptureSubscriptionOptions Options { get; }
    Task<CaptureOverflow> FirstOverflow { get; }
    CaptureSubscriptionMetrics Metrics { get; }
    Task Completion { get; }
    int Count { get; }
    bool TryRead(out T item);
    ValueTask<T> ReadAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> ReadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A bounded, role-aware channel reader with exact queue/drop metrics and explicit
/// lifetime. It derives from <see cref="ChannelReader{T}"/> so existing channel
/// consumption patterns continue to work while reads can update queue metrics.
/// </summary>
public sealed class CaptureSubscription<T> : ChannelReader<T>, ICaptureSubscription<T>
{
    private readonly object _gate = new();
    private readonly Channel<T> _channel;
    private readonly Action<CaptureSubscription<T>>? _onDisposed;
    private readonly Action<CaptureOverflow>? _onOverflow;
    private readonly TaskCompletionSource<CaptureOverflow> _firstOverflow =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _publishAttempts;
    private long _enqueued;
    private long _dequeued;
    private long _droppedOldest;
    private long _rejectedNew;
    private int _queueDepth;
    private int _highWatermark;
    private bool _completed;
    private bool _disposed;

    public CaptureSubscription(
        CaptureSubscriptionOptions options,
        bool singleWriter,
        Action<CaptureSubscription<T>>? onDisposed = null,
        Action<CaptureOverflow>? onOverflow = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Subscription capacity must be positive.");
        }

        Options = options;
        _onDisposed = onDisposed;
        _onOverflow = onOverflow;

        var channelOptions = new BoundedChannelOptions(options.Capacity)
        {
            FullMode = options.OverflowPolicy == CaptureOverflowPolicy.DropOldest
                ? BoundedChannelFullMode.DropOldest
                : BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = singleWriter,
            AllowSynchronousContinuations = false,
        };

        // The callback is the only reliable way to count a DropOldest eviction:
        // TryWrite returns true because the replacement item was accepted.
        _channel = Channel.CreateBounded<T>(channelOptions, OnOldestItemDropped);
    }

    public CaptureSubscriptionOptions Options { get; }

    /// <summary>Completes on the first queue eviction or rejected recording item.</summary>
    public Task<CaptureOverflow> FirstOverflow => _firstOverflow.Task;

    public CaptureSubscriptionMetrics Metrics
    {
        get
        {
            lock (_gate)
            {
                return Snapshot_NoLock();
            }
        }
    }

    public override Task Completion => _channel.Reader.Completion;
    public override bool CanCount => true;
    public override int Count => Metrics.QueueDepth;
    public override bool CanPeek => _channel.Reader.CanPeek;

    /// <summary>
    /// Publishes without blocking a hardware callback. A full recording queue returns
    /// false and signals an exact rejection; preview/monitor queues evict their oldest item.
    /// </summary>
    public bool TryPublish(T item)
    {
        CaptureOverflow? overflow = null;
        bool accepted;

        lock (_gate)
        {
            if (_completed || _disposed)
            {
                return false;
            }

            _publishAttempts++;
            var droppedBefore = _droppedOldest;
            accepted = _channel.Writer.TryWrite(item);

            if (accepted)
            {
                _enqueued++;
                _queueDepth++;
                if (_queueDepth > _highWatermark)
                {
                    _highWatermark = _queueDepth;
                }

                if (_droppedOldest != droppedBefore)
                {
                    overflow = CreateOverflow_NoLock(CaptureOverflowKind.OldestItemDropped);
                }
            }
            else
            {
                // FullMode.Wait + nonblocking TryWrite is the reject-new recording policy.
                _rejectedNew++;
                overflow = CreateOverflow_NoLock(CaptureOverflowKind.NewItemRejected);
                if (Options.OverflowPolicy == CaptureOverflowPolicy.RejectNew)
                {
                    // A strict consumer may only drain the contiguous prefix that was
                    // accepted before the first loss. Reopening admission after space
                    // becomes available would put post-gap media into the output and
                    // make the loss counters ambiguous.
                    _completed = true;
                    _channel.Writer.TryComplete();
                }
            }
        }

        if (overflow is not null)
        {
            SignalOverflow(overflow);
        }

        return accepted;
    }

    public void Complete(Exception? error = null)
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _channel.Writer.TryComplete(error);
        }
    }

    public override bool TryRead(out T item)
    {
        lock (_gate)
        {
            if (!_channel.Reader.TryRead(out item!)) return false;
            _dequeued++;
            _queueDepth--;
            return true;
        }
    }

    public override bool TryPeek(out T item)
    {
        lock (_gate)
        {
            return _channel.Reader.TryPeek(out item!);
        }
    }

    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.WaitToReadAsync(cancellationToken);

    private void OnOldestItemDropped(T _)
    {
        // Called synchronously from TryWrite. Monitor locks are re-entrant, so this
        // safely adjusts depth before TryPublish accounts for the replacement item.
        lock (_gate)
        {
            _droppedOldest++;
            _queueDepth--;
        }
    }

    private CaptureOverflow CreateOverflow_NoLock(CaptureOverflowKind kind) => new()
    {
        Kind = kind,
        Role = Options.Role,
        Timestamp100ns = CaptureMonotonicClock.GetTimestamp100ns(),
        OverflowCount = _droppedOldest + _rejectedNew,
    };

    private void SignalOverflow(CaptureOverflow overflow)
    {
        _firstOverflow.TrySetResult(overflow);
        try
        {
            _onOverflow?.Invoke(overflow);
        }
        catch (Exception)
        {
            // Telemetry observers must never unwind a hardware capture callback.
        }
    }

    private CaptureSubscriptionMetrics Snapshot_NoLock() => new(
        _publishAttempts,
        _enqueued,
        _dequeued,
        _droppedOldest,
        _rejectedNew,
        _queueDepth,
        _highWatermark);

    public void Dispose()
    {
        Action<CaptureSubscription<T>>? callback;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _completed = true;
            _channel.Writer.TryComplete();
            callback = _onDisposed;
        }

        // Never call back into a capture service while holding the subscription lock.
        callback?.Invoke(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
