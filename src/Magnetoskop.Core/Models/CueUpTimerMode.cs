namespace Magnetoskop.Core.Models;

/// <summary>
/// Timer system for Cue Up With Data (Sony 9-pin Timer Mode Select <c>41 36</c>).
/// </summary>
public enum CueUpTimerMode : byte
{
    /// <summary>DATA-1 = 00 — TIME CODE (LTC/VITC domain).</summary>
    TimeCode = 0x00,
    /// <summary>DATA-1 = 01 — TIMER-1 (CTL counter).</summary>
    Timer1 = 0x01,
}
