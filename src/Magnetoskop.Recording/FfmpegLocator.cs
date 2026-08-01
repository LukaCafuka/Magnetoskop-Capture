using System.Diagnostics;

namespace Magnetoskop.Recording;

/// <summary>Finds the ffmpeg executable: explicit setting → app folder → PATH.</summary>
public static class FfmpegLocator
{
    /// <summary>Optional explicit path configured by the user (takes precedence).</summary>
    public static string? ConfiguredPath { get; set; }

    public static string? Find()
    {
        if (!string.IsNullOrEmpty(ConfiguredPath) && File.Exists(ConfiguredPath))
        {
            return ConfiguredPath;
        }

        // Next to the application binaries.
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;

        var localSubdir = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(localSubdir)) return localSubdir;

        // PATH lookup.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // malformed PATH entry — skip
            }
        }

        return null;
    }

    /// <summary>Runs "ffmpeg -version" and returns the first line, or null when unusable.</summary>
    public static async Task<string?> ProbeVersionAsync(string ffmpegPath, CancellationToken ct = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;
            var firstLine = await process.StandardOutput.ReadLineAsync(ct);
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 ? firstLine : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}