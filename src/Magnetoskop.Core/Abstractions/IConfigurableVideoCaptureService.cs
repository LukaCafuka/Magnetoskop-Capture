using Magnetoskop.Core.Models;

namespace Magnetoskop.Core.Abstractions;

/// <summary>
/// Optional capability for capture services that can apply an operator-declared
/// input standard and scan order before <see cref="IVideoCaptureService.StartAsync"/>.
/// </summary>
public interface IConfigurableVideoCaptureService
{
    VideoInputConfiguration InputConfiguration { get; set; }

    /// <summary>Latest driver readback for preflight/UI acknowledgment.</summary>
    VideoInputFormatStatus FormatStatus { get; }
}
