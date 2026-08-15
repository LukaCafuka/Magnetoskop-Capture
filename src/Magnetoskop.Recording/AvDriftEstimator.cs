namespace Magnetoskop.Recording;

/// <summary>One snapshot of the video-master clock comparison.</summary>
public sealed record AvDriftSnapshot
{
    public bool IsReady { get; init; }
    public TimeSpan? InitialOffset { get; init; }
    public TimeSpan? CurrentOffset { get; init; }
    public double EstimatedDriftPpm { get; init; }
    public double RequiredCorrectionPpm { get; init; }
    public double AppliedCorrectionPpm { get; init; }
}

/// <summary>
/// Estimates audio-device drift relative to the video clock using sliding least-squares
/// regressions. Capture timestamps must share one monotonic domain. Audio correction is
/// clamped and slewed so callback jitter cannot create audible ratio steps.
/// </summary>
public sealed class AvDriftEstimator
{
    private readonly object _gate = new();
    private readonly double _videoFrameRate;
    private readonly double _audioSampleRate;
    private readonly TimeSpan _warmup;
    private readonly TimeSpan _window;
    private readonly TimeSpan _updateInterval;
    private readonly double _correctionClampPpm;
    private readonly TimeSpan _phaseCorrectionTimeConstant;
    private readonly Queue<Observation> _video = new();
    private readonly Queue<Observation> _audio = new();

    private long? _firstVideoIndex;
    private long? _firstAudioIndex;
    private long? _firstVideoTimestamp100ns;
    private long? _firstAudioTimestamp100ns;
    private long? _lastUpdateTimestamp100ns;
    private double _correctionIntegralSeconds;
    private double _actualAppliedCorrectionPpm;
    private AvDriftSnapshot _snapshot = new();

    public AvDriftEstimator(
        double videoFrameRate,
        double audioSampleRate,
        TimeSpan? warmup = null,
        TimeSpan? window = null,
        TimeSpan? updateInterval = null,
        double correctionClampPpm = 1000,
        double slewPpmPerSecond = 50,
        TimeSpan? phaseCorrectionTimeConstant = null)
    {
        if (videoFrameRate <= 0) throw new ArgumentOutOfRangeException(nameof(videoFrameRate));
        if (audioSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(audioSampleRate));
        if (correctionClampPpm <= 0) throw new ArgumentOutOfRangeException(nameof(correctionClampPpm));
        if (slewPpmPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(slewPpmPerSecond));
        if (phaseCorrectionTimeConstant is { } phase && phase <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(phaseCorrectionTimeConstant));

        _videoFrameRate = videoFrameRate;
        _audioSampleRate = audioSampleRate;
        _warmup = warmup ?? TimeSpan.FromSeconds(10);
        _window = window ?? TimeSpan.FromSeconds(30);
        _updateInterval = updateInterval ?? TimeSpan.FromSeconds(1);
        _correctionClampPpm = correctionClampPpm;
        _phaseCorrectionTimeConstant = phaseCorrectionTimeConstant ?? TimeSpan.FromSeconds(30);
    }

    public AvDriftSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public AvDriftSnapshot ObserveVideo(long timestamp100ns, long frameIndex)
    {
        lock (_gate)
        {
            _firstVideoTimestamp100ns ??= timestamp100ns;
            _firstVideoIndex ??= frameIndex;
            Add(_video, timestamp100ns,
                (frameIndex - _firstVideoIndex.Value) / _videoFrameRate);
            return Update(timestamp100ns);
        }
    }

    public AvDriftSnapshot ObserveAudio(long timestamp100ns, long firstSampleIndex)
    {
        lock (_gate)
        {
            _firstAudioTimestamp100ns ??= timestamp100ns;
            _firstAudioIndex ??= firstSampleIndex;
            Add(_audio, timestamp100ns,
                (firstSampleIndex - _firstAudioIndex.Value) / _audioSampleRate);
            return Update(timestamp100ns);
        }
    }

    /// <summary>
    /// Feeds back the correction actually reached by the adaptive resampler. The
    /// resampler is the sole slew/clamp authority, so residual integration cannot
    /// diverge from the PCM that was emitted.
    /// </summary>
    public AvDriftSnapshot ReportAppliedCorrection(double appliedCorrectionPpm)
    {
        lock (_gate)
        {
            _actualAppliedCorrectionPpm = appliedCorrectionPpm;
            _snapshot = _snapshot with { AppliedCorrectionPpm = appliedCorrectionPpm };
            return _snapshot;
        }
    }

    private void Add(Queue<Observation> observations, long timestamp100ns, double mediaSeconds)
    {
        observations.Enqueue(new Observation(timestamp100ns / 10_000_000.0, mediaSeconds));
        var cutoff = timestamp100ns / 10_000_000.0 - _window.TotalSeconds;
        while (observations.Count > 0 && observations.Peek().HostSeconds < cutoff)
            observations.Dequeue();
    }

    private AvDriftSnapshot Update(long now100ns)
    {
        TimeSpan? initialOffset = null;
        if (_firstVideoTimestamp100ns is { } videoStart && _firstAudioTimestamp100ns is { } audioStart)
            initialOffset = TimeSpan.FromTicks(audioStart - videoStart);

        if (_video.Count < 2 || _audio.Count < 2 || initialOffset is null)
        {
            _snapshot = _snapshot with { InitialOffset = initialOffset };
            return _snapshot;
        }

        var commonStart = Math.Max(_firstVideoTimestamp100ns!.Value, _firstAudioTimestamp100ns!.Value);
        if (now100ns - commonStart < _warmup.Ticks)
        {
            _snapshot = _snapshot with { InitialOffset = initialOffset };
            return _snapshot;
        }

        if (_lastUpdateTimestamp100ns is { } last
            && now100ns - last < _updateInterval.Ticks)
            return _snapshot;

        var videoLine = Fit(_video);
        var audioLine = Fit(_audio);
        if (videoLine.Slope <= 0 || audioLine.Slope <= 0)
            return _snapshot;

        var elapsedSeconds = _lastUpdateTimestamp100ns is { } previous
            ? Math.Max(0, (now100ns - previous) / 10_000_000.0)
            : _updateInterval.TotalSeconds;
        _correctionIntegralSeconds +=
            _actualAppliedCorrectionPpm * elapsedSeconds / 1_000_000.0;

        var hostSeconds = now100ns / 10_000_000.0;
        var videoPosition = videoLine.Intercept + videoLine.Slope * hostSeconds;
        var audioPosition = initialOffset.Value.TotalSeconds
            + audioLine.Intercept + audioLine.Slope * hostSeconds;
        var correctedResidualSeconds = audioPosition - videoPosition - _correctionIntegralSeconds;
        var estimatedPpm = (audioLine.Slope / videoLine.Slope - 1.0) * 1_000_000.0;
        var requiredPpm = estimatedPpm
            + correctedResidualSeconds / _phaseCorrectionTimeConstant.TotalSeconds * 1_000_000.0;

        _lastUpdateTimestamp100ns = now100ns;
        _snapshot = new AvDriftSnapshot
        {
            IsReady = true,
            InitialOffset = initialOffset,
            CurrentOffset = TimeSpan.FromSeconds(correctedResidualSeconds),
            EstimatedDriftPpm = estimatedPpm,
            RequiredCorrectionPpm = requiredPpm,
            AppliedCorrectionPpm = _actualAppliedCorrectionPpm,
        };
        return _snapshot;
    }

    private static Regression Fit(IEnumerable<Observation> observations)
    {
        var values = observations.ToArray();
        var meanX = values.Average(x => x.HostSeconds);
        var meanY = values.Average(x => x.MediaSeconds);
        double covariance = 0;
        double variance = 0;
        foreach (var value in values)
        {
            var dx = value.HostSeconds - meanX;
            covariance += dx * (value.MediaSeconds - meanY);
            variance += dx * dx;
        }
        var slope = variance > double.Epsilon ? covariance / variance : 0;
        return new Regression(slope, meanY - slope * meanX);
    }

    private readonly record struct Observation(double HostSeconds, double MediaSeconds);
    private readonly record struct Regression(double Slope, double Intercept);
}
