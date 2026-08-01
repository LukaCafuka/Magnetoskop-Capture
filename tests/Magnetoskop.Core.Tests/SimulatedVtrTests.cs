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
            () => _vtr.SendTransportCommandAsync(TransportCommand.JogForward));
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