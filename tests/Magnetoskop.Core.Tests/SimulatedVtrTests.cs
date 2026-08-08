using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Magnetoskop.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.Core.Tests;

public class SimulatedVtrTests : IAsyncLifetime
{
    private readonly SimulatedVtr _vtr = new(NullLogger<SimulatedVtr>.Instance);

    public async Task InitializeAsync() => await _vtr.ConnectAsync();

    public async Task DisposeAsync() => await _vtr.DisposeAsync();

    [Fact]
    public async Task Play_SetsPlayingState()
    {
        await _vtr.SendTransportCommandAsync(TransportCommand.Play);
        var status = await WaitForStatusAsync(s => s.Transport == TransportState.Playing);
        Assert.Equal(TransportState.Playing, status.Transport);
        Assert.True(status.ServoLock);
    }

    [Fact]
    public async Task Stop_AfterPlay_SetsStoppedState()
    {
        await _vtr.SendTransportCommandAsync(TransportCommand.Play);
        await WaitForStatusAsync(s => s.Transport == TransportState.Playing);
        await _vtr.SendTransportCommandAsync(TransportCommand.Stop);
        var status = await WaitForStatusAsync(s => s.Transport == TransportState.Stopped);
        Assert.Equal(TransportState.Stopped, status.Transport);
    }

    [Fact]
    public async Task Pause_AfterPlay_SetsStillState()
    {
        await _vtr.SendTransportCommandAsync(TransportCommand.Play);
        await WaitForStatusAsync(s => s.Transport == TransportState.Playing);
        await _vtr.SendTransportCommandAsync(TransportCommand.Pause);
        var status = await WaitForStatusAsync(s => s.Transport == TransportState.Still);
        Assert.Equal(TransportState.Still, status.Transport);
    }

    [Fact]
    public async Task FrameStepForward_AdvancesOneFrameAndStills()
    {
        var before = await WaitForTimeAsync(t => t.Ctl is not null);
        var beforeFrames = before.Ctl!.Value.ToFrameCount(25);

        await _vtr.SendTransportCommandAsync(TransportCommand.FrameStepForward);

        var status = await WaitForStatusAsync(s => s.Transport == TransportState.Still);
        Assert.Equal(TransportState.Still, status.Transport);
        var after = await WaitForTimeAsync(t =>
            t.Ctl is { } ctl && ctl.ToFrameCount(25) == beforeFrames + 1);
        Assert.Equal(beforeFrames + 1, after.Ctl!.Value.ToFrameCount(25));
        Assert.Equal(beforeFrames + 1 + 1L * 60 * 60 * 25, after.Ltc!.Value.ToFrameCount(25));
    }

    [Fact]
    public async Task FrameStepReverse_MovesBackOneFrameAndStills()
    {
        await _vtr.SendTransportCommandAsync(TransportCommand.FrameStepForward);
        await WaitForTimeAsync(t => t.Ctl is { } c && c.ToFrameCount(25) >= 1);
        var before = _vtr.CurrentTime;
        var beforeFrames = before.Ctl!.Value.ToFrameCount(25);

        await _vtr.SendTransportCommandAsync(TransportCommand.FrameStepReverse);

        var status = await WaitForStatusAsync(s => s.Transport == TransportState.Still);
        Assert.Equal(TransportState.Still, status.Transport);
        var after = await WaitForTimeAsync(t =>
            t.Ctl is { } ctl && ctl.ToFrameCount(25) == beforeFrames - 1);
        Assert.Equal(beforeFrames - 1, after.Ctl!.Value.ToFrameCount(25));
    }

    [Fact]
    public async Task Timer1Reset_ZeroesCtlWithoutSeekingLtc()
    {
        await _vtr.SendTransportCommandAsync(TransportCommand.Play);
        var before = await WaitForTimeAsync(t =>
            t.Ctl is { } ctl && ctl.ToFrameCount(25) >= 10 && t.Ltc is not null);
        var ltcBefore = before.Ltc!.Value.ToFrameCount(25);

        await _vtr.SendTransportCommandAsync(TransportCommand.Stop);
        await _vtr.SendTransportCommandAsync(TransportCommand.Timer1Reset);

        var after = await WaitForTimeAsync(t =>
            t.Ctl is { } ctl && ctl.ToFrameCount(25) == 0);
        Assert.Equal(0, after.Ctl!.Value.ToFrameCount(25));
        // Tape did not seek — LTC stays at the same absolute position.
        Assert.Equal(ltcBefore, after.Ltc!.Value.ToFrameCount(25));
    }

    [Fact]
    public async Task Play_AdvancesTimecode()
    {
        await _vtr.SendTransportCommandAsync(TransportCommand.Play);
        var first = await WaitForTimeAsync(t => t.Ctl is not null);
        var later = await WaitForTimeAsync(t =>
            t.Ctl is { } ctl && ctl.ToFrameCount(25) > first.Ctl!.Value.ToFrameCount(25));
        Assert.True(later.Ctl!.Value.ToFrameCount(25) > first.Ctl!.Value.ToFrameCount(25));
    }

    [Fact]
    public async Task Play_ReportsLtcWithOffset()
    {
        await _vtr.SendTransportCommandAsync(TransportCommand.Play);
        var time = await WaitForTimeAsync(t => t.Ltc is not null);
        // LTC starts at 01:00:00:00 in the simulation.
        Assert.True(time.Ltc!.Value.Hours >= 1);
        Assert.Equal(TimecodeSource.Ltc, time.PrimarySource);
    }

    [Fact]
    public async Task FastForward_HidesLtcAndVitc()
    {
        await _vtr.SendTransportCommandAsync(TransportCommand.FastForward);
        var time = await WaitForTimeAsync(t => t.PrimarySource == TimecodeSource.CorrectedLtc);
        Assert.Null(time.Ltc);
        Assert.Null(time.Vitc);
        Assert.NotNull(time.Ctl);
    }

    [Fact]
    public async Task Eject_SetsTapeOut_AndCommandsAfterwardsFail()
    {
        await _vtr.SendTransportCommandAsync(TransportCommand.Eject);
        var status = await WaitForStatusAsync(s => s.TapeOut);
        Assert.True(status.TapeOut);

        await Assert.ThrowsAsync<VtrCommunicationException>(
            () => _vtr.SendTransportCommandAsync(TransportCommand.Play));
    }

    [Fact]
    public async Task InsertTape_RestoresOperation()
    {
        await _vtr.SendTransportCommandAsync(TransportCommand.Eject);
        await WaitForStatusAsync(s => s.TapeOut);

        _vtr.InsertTape();
        await WaitForStatusAsync(s => !s.TapeOut);
        await _vtr.SendTransportCommandAsync(TransportCommand.Play);
        var status = await WaitForStatusAsync(s => s.Transport == TransportState.Playing);
        Assert.Equal(TransportState.Playing, status.Transport);
    }

    [Fact]
    public async Task UnimplementedCommand_ThrowsUnsupportedCommandException()
    {
        await Assert.ThrowsAsync<UnsupportedCommandException>(
            () => _vtr.SendTransportCommandAsync(TransportCommand.CueUp));
    }

    [Fact]
    public async Task CueUp_SeeksToLtcAndSetsStill()
    {
        // LTC = CTL + 01:00:00:00 offset → cue to 01:10:00:00 → CTL 00:10:00:00
        await _vtr.CueUpAsync(new Timecode(1, 10, 0, 0), CueUpTimerMode.TimeCode);
        var status = await WaitForStatusAsync(s => s.Transport == TransportState.Still);
        Assert.Equal(TransportState.Still, status.Transport);

        var time = await WaitForTimeAsync(t =>
            t.Ltc is { } ltc && ltc.Hours == 1 && ltc.Minutes == 10);
        Assert.Equal(new Timecode(1, 10, 0, 0), time.Ltc);
        Assert.Equal(new Timecode(0, 10, 0, 0), time.Ctl);
    }

    [Fact]
    public async Task CueUp_Timer1_Negative_SeeksBelowCtlOrigin()
    {
        // Park past BOT, zero CTL there, then cue 10 frames before the origin.
        await _vtr.CueUpAsync(new Timecode(0, 0, 1, 0), CueUpTimerMode.Timer1);
        await WaitForTimeAsync(t => t.Ctl is { } c && c.ToFrameCount(25) == 25);
        await _vtr.SendTransportCommandAsync(TransportCommand.Timer1Reset);
        await WaitForTimeAsync(t => t.Ctl is { } c && c.ToFrameCount(25) == 0);

        await _vtr.CueUpAsync(new Timecode(0, 0, 0, 10, IsNegative: true), CueUpTimerMode.Timer1);

        var after = await WaitForTimeAsync(t =>
            t.Ctl is { } ctl && ctl.IsNegative && ctl.ToFrameCount(25) == -10);
        Assert.Equal(-10, after.Ctl!.Value.ToFrameCount(25));
    }

    [Fact]
    public async Task CueUp_Timer1_SeeksToCtl()
    {
        await _vtr.CueUpAsync(new Timecode(0, 12, 0, 0), CueUpTimerMode.Timer1);
        var status = await WaitForStatusAsync(s => s.Transport == TransportState.Still);
        Assert.Equal(TransportState.Still, status.Transport);

        var time = await WaitForTimeAsync(t =>
            t.Ctl is { } ctl && ctl.Minutes == 12 && ctl.Hours == 0);
        Assert.Equal(new Timecode(0, 12, 0, 0), time.Ctl);
    }

    [Fact]
    public async Task ShuttleForward_SetsShuttleState_AndAdvancesCtl()
    {
        var first = await WaitForTimeAsync(t => t.Ctl is not null);
        await _vtr.SendVariableSpeedAsync(VariableSpeedMode.Shuttle, forward: true, speed: 64);
        var status = await WaitForStatusAsync(s => s.Transport == TransportState.Shuttle);
        Assert.Equal(TransportState.Shuttle, status.Transport);

        var later = await WaitForTimeAsync(t =>
            t.Ctl is { } ctl && ctl.ToFrameCount(25) > first.Ctl!.Value.ToFrameCount(25));
        Assert.True(later.Ctl!.Value.ToFrameCount(25) > first.Ctl!.Value.ToFrameCount(25));
    }

    [Fact]
    public async Task JogReverse_SetsJogState()
    {
        // Move forward first so reverse has room before BOT.
        await _vtr.SendTransportCommandAsync(TransportCommand.Play);
        await Task.Delay(200);
        await _vtr.SendTransportCommandAsync(TransportCommand.Stop);

        await _vtr.SendVariableSpeedAsync(VariableSpeedMode.Jog, forward: false, speed: 48);
        var status = await WaitForStatusAsync(s => s.Transport == TransportState.Jog);
        Assert.Equal(TransportState.Jog, status.Transport);
        Assert.True(status.TapeReverse);
    }

    [Fact]
    public async Task Command_WhenDisconnected_ThrowsCommunicationException()
    {
        await _vtr.DisconnectAsync();
        await Assert.ThrowsAsync<VtrCommunicationException>(
            () => _vtr.SendTransportCommandAsync(TransportCommand.Play));
    }

    // ---- helpers -------------------------------------------------------

    private async Task<VtrStatus> WaitForStatusAsync(Func<VtrStatus, bool> predicate, int timeoutMs = 2000)
    {
        var tcs = new TaskCompletionSource<VtrStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, VtrStatus status)
        {
            if (predicate(status)) tcs.TrySetResult(status);
        }

        _vtr.StatusChanged += Handler;
        try
        {
            if (predicate(_vtr.CurrentStatus)) return _vtr.CurrentStatus;
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            Assert.True(completed == tcs.Task, "Timed out waiting for expected VTR status.");
            return await tcs.Task;
        }
        finally
        {
            _vtr.StatusChanged -= Handler;
        }
    }

    private async Task<TimeInformation> WaitForTimeAsync(Func<TimeInformation, bool> predicate, int timeoutMs = 2000)
    {
        var tcs = new TaskCompletionSource<TimeInformation>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, TimeInformation time)
        {
            if (predicate(time)) tcs.TrySetResult(time);
        }

        _vtr.TimeChanged += Handler;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            Assert.True(completed == tcs.Task, "Timed out waiting for expected VTR time information.");
            return await tcs.Task;
        }
        finally
        {
            _vtr.TimeChanged -= Handler;
        }
    }
}