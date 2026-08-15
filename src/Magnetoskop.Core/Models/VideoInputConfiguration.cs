namespace Magnetoskop.Core.Models;

/// <summary>Operator-selected input standard. Unspecified never implies signal analysis.</summary>
public enum VideoInputStandard
{
    Unspecified,
    Pal,
    Ntsc,
    Custom,
}

/// <summary>Operator-selected scan structure and field order.</summary>
public enum VideoScanMode
{
    Unspecified,
    Progressive,
    Tff,
    Bff,
}

/// <summary>
/// Capture-device input declaration. PAL and NTSC request their conventional SD
/// dimensions/rates; Custom uses the explicit numeric fields.
/// </summary>
public sealed record VideoInputConfiguration
{
    public VideoInputStandard Standard { get; init; }
    public VideoScanMode ScanMode { get; init; }
    public int CustomWidth { get; init; } = 720;
    public int CustomHeight { get; init; } = 576;
    public double CustomFrameRate { get; init; } = 25;

    public bool IsExplicit
        => Standard != VideoInputStandard.Unspecified
           && ScanMode != VideoScanMode.Unspecified
           && (Standard != VideoInputStandard.Custom || HasValidCustomFormat);

    public bool HasValidCustomFormat
        => CustomWidth > 0
           && CustomHeight > 0
           && double.IsFinite(CustomFrameRate)
           && CustomFrameRate > 0;

    public int? RequestedWidth => Standard switch
    {
        VideoInputStandard.Pal => 720,
        VideoInputStandard.Ntsc => 720,
        VideoInputStandard.Custom when HasValidCustomFormat => CustomWidth,
        _ => null,
    };

    public int? RequestedHeight => Standard switch
    {
        VideoInputStandard.Pal => 576,
        VideoInputStandard.Ntsc => 480,
        VideoInputStandard.Custom when HasValidCustomFormat => CustomHeight,
        _ => null,
    };

    public double? RequestedFrameRate => Standard switch
    {
        VideoInputStandard.Pal => 25,
        VideoInputStandard.Ntsc => 30_000d / 1_001d,
        VideoInputStandard.Custom when HasValidCustomFormat => CustomFrameRate,
        _ => null,
    };

    public bool Interlaced => ScanMode is VideoScanMode.Tff or VideoScanMode.Bff;
    public bool TopFieldFirst => ScanMode != VideoScanMode.Bff;

    public static VideoInputConfiguration SimulatedPalTff { get; } = new()
    {
        Standard = VideoInputStandard.Pal,
        ScanMode = VideoScanMode.Tff,
    };
}
