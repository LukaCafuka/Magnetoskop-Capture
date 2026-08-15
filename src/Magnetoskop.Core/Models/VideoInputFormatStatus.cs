namespace Magnetoskop.Core.Models;

/// <summary>
/// Readback of the operator request versus the format actually opened by the driver.
/// Acknowledgment is an application decision and is intentionally not stored here.
/// </summary>
public sealed record VideoInputFormatStatus
{
    public required VideoInputConfiguration Requested { get; init; }
    /// <summary>
    /// Size and frame rate reported by the opened source. When
    /// <see cref="ScanReadbackAvailable"/> is false, the scan fields are the
    /// operator declaration (or legacy fallback), not a driver measurement.
    /// </summary>
    public VideoFormat? Actual { get; init; }
    /// <summary>True only when the backend can measure scan mode and field order.</summary>
    public bool ScanReadbackAvailable { get; init; }
    public bool MatchesRequested { get; init; }
    public bool AcknowledgmentRequired { get; init; } = true;
    public string? Message { get; init; }

    /// <summary>
    /// Builds the persisted acknowledgement fingerprint for one stable device.
    /// Any requested format, actual readback, scan provenance, or stable identity
    /// change invalidates the previous operator acknowledgement.
    /// </summary>
    public string BuildAcknowledgmentKey(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var actual = Actual
                     ?? throw new InvalidOperationException(
                         "An actual driver-format readback is required before acknowledgement.");
        var requested = Requested;
        FormattableString fingerprint = $"v1|device:{deviceId.Length}:{deviceId}|requested:{requested.Standard}:{requested.ScanMode}:{requested.RequestedWidth}x{requested.RequestedHeight}@{requested.RequestedFrameRate:R}|actual:{actual.Width}x{actual.Height}@{actual.FrameRate:R}:{actual.PixelFormat}:{ScanReadbackAvailable}:{actual.Interlaced}:{actual.TopFieldFirst}";
        return FormattableString.Invariant(fingerprint);
    }

    public static VideoInputFormatStatus Pending(VideoInputConfiguration requested) => new()
    {
        Requested = requested,
        Actual = null,
        ScanReadbackAvailable = false,
        MatchesRequested = false,
        AcknowledgmentRequired = true,
        Message = "The capture-device format has not been read back yet.",
    };

    public static VideoInputFormatStatus FromReadback(
        VideoInputConfiguration requested,
        VideoFormat actual,
        bool scanReadbackAvailable = false,
        double frameRateTolerance = 0.05)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(actual);

        var dimensionsMatch = requested.RequestedWidth == actual.Width
                              && requested.RequestedHeight == actual.Height;
        var frameRateMatches = requested.RequestedFrameRate is { } requestedRate
                               && Math.Abs(requestedRate - actual.FrameRate) <= frameRateTolerance;
        // An unavailable scan readback is unknown, not a mismatch. It still
        // requires an explicit operator acknowledgment below.
        var scanMatches = !scanReadbackAvailable
                          || requested.ScanMode != VideoScanMode.Unspecified
                             && requested.Interlaced == actual.Interlaced
                             && (!actual.Interlaced
                                 || requested.TopFieldFirst == actual.TopFieldFirst);
        var matches = requested.IsExplicit && dimensionsMatch && frameRateMatches && scanMatches;

        return new VideoInputFormatStatus
        {
            Requested = requested,
            Actual = actual,
            ScanReadbackAvailable = scanReadbackAvailable,
            MatchesRequested = matches,
            AcknowledgmentRequired = !matches || !scanReadbackAvailable,
            Message = !requested.IsExplicit
                ? "The video standard and scan order are not fully specified."
                : !matches
                    ? "The capture driver readback does not match the requested input format."
                    : !scanReadbackAvailable
                        ? "Frame size and rate match, but the backend cannot read back scan structure or field order."
                        : null,
        };
    }
}
