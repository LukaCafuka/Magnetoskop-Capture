namespace Magnetoskop.Core.Models;

/// <summary>
/// A frame-accurate timecode value (HH:MM:SS:FF).
/// Sony 9-pin transports time as BCD; this type stores plain integers
/// and provides conversion helpers at the protocol layer.
/// </summary>
public readonly record struct Timecode(int Hours, int Minutes, int Seconds, int Frames, bool DropFrame = false, bool ColorFrame = false)
{
    public static readonly Timecode Zero = new(0, 0, 0, 0);

    public override string ToString()
        => $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}{(DropFrame ? ';' : ':')}{Frames:D2}";

    /// <summary>Total frame count assuming the given nominal frame rate (no drop-frame math).</summary>
    public long ToFrameCount(int framesPerSecond)
        => ((Hours * 60L + Minutes) * 60L + Seconds) * framesPerSecond + Frames;

    public static Timecode FromFrameCount(long totalFrames, int framesPerSecond)
    {
        if (totalFrames < 0) totalFrames = 0;
        var frames = (int)(totalFrames % framesPerSecond);
        var totalSeconds = totalFrames / framesPerSecond;
        var seconds = (int)(totalSeconds % 60);
        var totalMinutes = totalSeconds / 60;
        var minutes = (int)(totalMinutes % 60);
        var hours = (int)(totalMinutes / 60 % 24);
        return new Timecode(hours, minutes, seconds, frames);
    }
}