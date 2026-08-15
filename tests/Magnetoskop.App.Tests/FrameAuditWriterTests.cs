using System.IO;
using System.Text.Json;
using Magnetoskop.App.Services;

namespace Magnetoskop.App.Tests;

public sealed class FrameAuditWriterTests
{
    [Fact]
    public async Task Complete_WritesEveryAcceptedRecordAsJsonLine()
    {
        var directory = Directory.CreateTempSubdirectory("magnetoskop-audit-test");
        var path = System.IO.Path.Combine(directory.FullName, "capture.frames.jsonl");
        try
        {
            await using var writer = new FrameAuditWriter(path, capacity: 8);
            for (var i = 0; i < 5; i++)
            {
                Assert.True(writer.TryWrite(CreateRecord(i)));
            }

            await writer.CompleteAsync();

            Assert.Equal(5, writer.Accepted);
            Assert.Equal(5, writer.Written);
            Assert.Equal(0, writer.Rejected);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(5, lines.Length);
            using var first = JsonDocument.Parse(lines[0]);
            Assert.Equal(0, first.RootElement.GetProperty("recordingFrameNumber").GetInt64());
            Assert.Equal("01:00:00:00", first.RootElement.GetProperty("observedTimecode").GetString());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Saturation_RejectsInsteadOfSilentlyReplacingQueuedRecords()
    {
        var directory = Directory.CreateTempSubdirectory("magnetoskop-audit-test");
        var path = System.IO.Path.Combine(directory.FullName, "capture.frames.jsonl");
        try
        {
            await using var writer = new FrameAuditWriter(path, capacity: 1);
            var rejected = false;
            for (var i = 0; i < 100_000 && !rejected; i++)
            {
                rejected = !writer.TryWrite(CreateRecord(i));
            }

            Assert.True(rejected);
            await writer.Overflowed.WaitAsync(TimeSpan.FromSeconds(1));
            await writer.CompleteAsync();
            Assert.True(writer.Rejected >= 1);
            Assert.Equal(writer.Accepted, writer.Written);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static FrameAuditRecord CreateRecord(long frame) => new()
    {
        RecordingFrameNumber = frame,
        SourceFrameNumber = frame + 100,
        CaptureTimestamp100Ns = 10_000_000 + frame,
        CaptureOffsetMilliseconds = frame * 40,
        ObservedTimecode = "01:00:00:00",
        TimecodeSource = "Ltc",
        ObservationAgeMilliseconds = 20,
        TimecodeFreshness = "Fresh",
        ServoLocked = true,
    };
}
