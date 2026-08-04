namespace Magnetoskop.Core.Models;

/// <summary>
/// A frame-accurate timecode value (HH:MM:SS:FF).
/// Sony 9-pin transports time as BCD; this type stores plain integers
/// and provides conversion helpers at the protocol layer.
/// CTL counters may be negative (below zero); <see cref="IsNegative"/> and a
/// leading '-' in <see cref="ToString"/> represent that.
/// </summary>
public readonly record struct Timecode(
    int Hours,
    int Minutes,
    int Seconds,
    int Frames,
    bool DropFrame = false,
    bool ColorFrame = false,
    bool IsNegative = false)
{
    public static readonly Timecode Zero = new(0, 0, 0, 0);

    public override string ToString()
    {
        var body = $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}{(DropFrame ? ';' : ':')}{Frames:D2}";
        return IsNegative ? "-" + body : body;
    }

    /// <summary>Total frame count assuming the given nominal frame rate (no drop-frame math).</summary>
    public long ToFrameCount(int framesPerSecond)
    {
        var n = ((Hours * 60L + Minutes) * 60L + Seconds) * framesPerSecond + Frames;
        return IsNegative ? -n : n;
    }

    public static Timecode FromFrameCount(long totalFrames, int framesPerSecond)
    {
        var negative = totalFrames < 0;
        if (negative) totalFrames = -totalFrames;

        var frames = (int)(totalFrames % framesPerSecond);
        var totalSeconds = totalFrames / framesPerSecond;
        var seconds = (int)(totalSeconds % 60);
        var totalMinutes = totalSeconds / 60;
        var minutes = (int)(totalMinutes % 60);
        var hours = (int)(totalMinutes / 60 % 24);
        return new Timecode(hours, minutes, seconds, frames, IsNegative: negative);
    }

    /// <summary>
    /// Parses tape timecode for Cue Up.
    /// Accepts <c>HH:MM:SS:FF</c> / <c>HH:MM:SS;FF</c>, or short <c>MM:SS:FF</c> as <c>00:MM:SS:FF</c>.
    /// Leading zeros optional. When <paramref name="allowNegative"/> is true, a leading '-' sets
    /// <see cref="IsNegative"/> (CTL sign). Otherwise negatives are rejected.
    /// </summary>
    public static bool TryParse(string? text, out Timecode timecode, bool allowNegative = false)
    {
        timecode = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();
        var negative = false;
        if (text.StartsWith('-'))
        {
            if (!allowNegative) return false;
            negative = true;
            text = text[1..].TrimStart();
        }

        var dropFrame = text.Contains(';');
        var parts = text.Replace(';', ':').Split(':');

        int hours, minutes, seconds, frames;
        if (parts.Length == 4)
        {
            if (!int.TryParse(parts[0], out hours)
                || !int.TryParse(parts[1], out minutes)
                || !int.TryParse(parts[2], out seconds)
                || !int.TryParse(parts[3], out frames))
            {
                return false;
            }
        }
        else if (parts.Length == 3)
        {
            // MM:SS:FF → 00:MM:SS:FF (so "1:00:00" means 1 minute, not 1 hour).
            hours = 0;
            if (!int.TryParse(parts[0], out minutes)
                || !int.TryParse(parts[1], out seconds)
                || !int.TryParse(parts[2], out frames))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (hours is < 0 or > 23
            || minutes is < 0 or > 59
            || seconds is < 0 or > 59
            || frames is < 0 or > 29)
        {
            return false;
        }

        timecode = new Timecode(hours, minutes, seconds, frames, DropFrame: dropFrame, IsNegative: negative);
        return true;
    }

    /// <summary>
    /// Formats CTL for the UI. By default (wrap=false), values in the upper half of the
    /// 24h day are shown as negative (-HH:MM:SS:FF). When wrap=true, the deck's 24h wrap
    /// is shown as-is (and protocol-signed negatives are expanded back to wrap).
    /// </summary>
    public static string FormatCtlDisplay(Timecode? ctl, bool use24HourWrap, int framesPerSecond = 25)
    {
        if (ctl is null) return "--:--:--:--";

        var tc = ctl.Value;
        if (use24HourWrap)
        {
            if (tc.IsNegative && framesPerSecond > 0)
            {
                var dayFrames = 24L * 3600 * framesPerSecond;
                tc = FromFrameCount(dayFrames + tc.ToFrameCount(framesPerSecond), framesPerSecond);
            }

            return tc.ToString();
        }

        return InterpretAsSignedCtl(tc, framesPerSecond).ToString();
    }

    /// <summary>
    /// CTL on many decks wraps below zero as 24h BCD (e.g. -1 frame @ 25 fps → 23:59:59:24).
    /// Reinterpret the upper half of the day as a negative absolute value for display.
    /// Already-signed values (hours sign bit) are left unchanged.
    /// </summary>
    public static Timecode InterpretAsSignedCtl(Timecode tc, int framesPerSecond = 25)
    {
        if (tc.IsNegative || framesPerSecond <= 0) return tc;

        var dayFrames = 24L * 3600 * framesPerSecond;
        var frames = ((tc.Hours * 60L + tc.Minutes) * 60L + tc.Seconds) * framesPerSecond + tc.Frames;
        if (frames == 0 || frames <= dayFrames / 2) return tc;

        var abs = FromFrameCount(dayFrames - frames, framesPerSecond);
        return abs with { IsNegative = true, DropFrame = tc.DropFrame, ColorFrame = tc.ColorFrame };
    }
}
