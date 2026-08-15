using Magnetoskop.Core.Abstractions;
using Magnetoskop.Core.Models;
using Magnetoskop.Protocol.Sony9Pin;
using Magnetoskop.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magnetoskop.Compatibility.Tests;

/// <summary>
/// Runs the real Sony 9-pin protocol stack (controller + transceiver + parser)
/// against wire-level simulated versions of all five thesis target decks.
/// The deck side implements framing independently, so these tests exercise byte-exact
/// interop rather than the protocol project talking to itself.
/// </summary>
public class DeckCompatibilityTests
{
    public static IEnumerable<object[]> AllPersonalities()
        => DeckPersonalities.All.Select(p => new object[] { p.Id });

    private static DeckPersonality Personality(string id)
        => DeckPersonalities.All.Single(p => p.Id == id);

    private static VtrDeviceProfile ProfileFor(string id)
        => KnownDeviceProfiles.All.Single(p => p.Id == id);

    private static (Sony9PinController Controller, SimulatedDeckTransport Deck) CreateRig(
        DeckPersonality personality, VtrDeviceProfile? profile = null)
    {
        var deck = new SimulatedDeckTransport(personality);
        var controller = new Sony9PinController(
            deck,
            profile ?? ProfileFor(personality.Id),
            new SerialSettings { PortName = "SIM" },
            NullLogger<Sony9PinController>.Instance);
        return (controller, deck);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Condition not reached within the timeout.");
            }
            await Task.Delay(20);
        }
    }

    // ---- Connect + identification ------------------------------------------

    [Theory]
    [MemberData(nameof(AllPersonalities))]
    public async Task Connect_SucceedsOnEveryDeck(string id)
    {
        var (controller, deck) = CreateRig(Personality(id));
        await using var _ = controller;

        await controller.ConnectAsync();

        Assert.True(controller.IsConnected);
        Assert.True(deck.IsOpen);
    }

    [Theory]
    [MemberData(nameof(AllPersonalities))]
    public async Task Connect_UsesSonySerialSettings(string id)
    {
        var (controller, deck) = CreateRig(Personality(id));
        await using var _ = controller;

        await controller.ConnectAsync();

        Assert.NotNull(deck.OpenedSettings);
        Assert.Equal(38400, deck.OpenedSettings!.BaudRate);
        Assert.Equal(SerialParity.Odd, deck.OpenedSettings.Parity);
        Assert.Equal(8, deck.OpenedSettings.DataBits);
        Assert.Equal(1, deck.OpenedSettings.StopBits);
    }

    [Fact]
    public async Task Pvw2600P_DeviceTypeIsRecognized()
    {
        var (controller, _) = CreateRig(DeckPersonalities.Pvw2600P);
        await using var _1 = controller;

        await controller.ConnectAsync();

        Assert.NotNull(controller.DetectedProfile);
        Assert.Equal("sony-pvw-2600p", controller.DetectedProfile!.Id);
        Assert.Contains("21 40", controller.DeviceDescription);
    }

    [Fact]
    public async Task DvwM2000P_DeviceTypeIsRecognized()
    {
        var (controller, _) = CreateRig(DeckPersonalities.DvwM2000P);
        await using var _1 = controller;

        await controller.ConnectAsync();

        Assert.NotNull(controller.DetectedProfile);
        Assert.Equal("sony-dvw-m2000p", controller.DetectedProfile!.Id);
        Assert.Contains("B1 04", controller.DeviceDescription);
    }

    [Theory]
    [InlineData("sony-bvu-950p")]
    [InlineData("sony-uvw-1800p")]
    public async Task UnknownDeviceTypeCodes_FallBackToGenericProfile(string id)
    {
        var (controller, _) = CreateRig(Personality(id));
        await using var _1 = controller;

        await controller.ConnectAsync();

        // The placeholder device-type codes must not accidentally match a real profile.
        Assert.NotNull(controller.DetectedProfile);
        Assert.Equal(VtrDeviceProfile.Generic.Id, controller.DetectedProfile!.Id);
        Assert.True(controller.IsConnected);
    }

    [Fact]
    public async Task Jvc_ConnectsDespiteRefusingDeviceTypeRequest()
    {
        var (controller, _) = CreateRig(DeckPersonalities.JvcBrS622E);
        await using var _1 = controller;

        await controller.ConnectAsync();

        Assert.True(controller.IsConnected);
        Assert.Null(controller.DetectedProfile);
        // Falls back to the configured profile name.
        Assert.Contains("JVC BR-S622E", controller.DeviceDescription);
    }

    // ---- Transport commands ---------------------------------------------------

    [Theory]
    [MemberData(nameof(AllPersonalities))]
    public async Task RequiredTransportCommands_AcknowledgedByEveryDeck(string id)
    {
        var (controller, deck) = CreateRig(Personality(id));
        await using var _ = controller;
        await controller.ConnectAsync();

        await controller.SendTransportCommandAsync(TransportCommand.Play);
        Assert.Equal(TransportState.Playing, deck.TransportState);

        await controller.SendTransportCommandAsync(TransportCommand.FastForward);
        Assert.Equal(TransportState.FastForwarding, deck.TransportState);

        await controller.SendTransportCommandAsync(TransportCommand.Rewind);
        Assert.Equal(TransportState.Rewinding, deck.TransportState);

        await controller.SendTransportCommandAsync(TransportCommand.Stop);
        Assert.Equal(TransportState.Stopped, deck.TransportState);

        await controller.SendTransportCommandAsync(TransportCommand.Eject);
    }

    [Fact]
    public async Task Jvc_RejectsRecordAndLearnsIt()
    {
        var (controller, _) = CreateRig(DeckPersonalities.JvcBrS622E);
        await using var _1 = controller;
        await controller.ConnectAsync();

        // First attempt hits the wire and gets NAK "undefined command".
        await Assert.ThrowsAsync<UnsupportedCommandException>(
            () => controller.SendTransportCommandAsync(TransportCommand.Record));
        Assert.False(controller.Capabilities.IsSupported(TransportCommand.Record));

        // Second attempt is short-circuited by the learned capability.
        await Assert.ThrowsAsync<UnsupportedCommandException>(
            () => controller.SendTransportCommandAsync(TransportCommand.Record));

        // The required command set still works.
        await controller.SendTransportCommandAsync(TransportCommand.Play);
        Assert.True(controller.Capabilities.IsSupported(TransportCommand.Play));
    }

    // ---- Status polling ----------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllPersonalities))]
    public async Task StatusPolling_ReflectsTransportState(string id)
    {
        var (controller, _) = CreateRig(Personality(id));
        await using var _1 = controller;
        await controller.ConnectAsync();

        await WaitUntilAsync(() => controller.CurrentStatus.Transport == TransportState.Stopped);

        await controller.SendTransportCommandAsync(TransportCommand.Play);
        await WaitUntilAsync(() =>
            controller.CurrentStatus.Transport == TransportState.Playing
            && controller.CurrentStatus.ServoLock);
    }

    [Theory]
    [MemberData(nameof(AllPersonalities))]
    public async Task Eject_RaisesTapeOutFlag(string id)
    {
        var (controller, _) = CreateRig(Personality(id));
        await using var _1 = controller;
        await controller.ConnectAsync();

        await controller.SendTransportCommandAsync(TransportCommand.Eject);

        await WaitUntilAsync(() => controller.CurrentStatus.TapeOut);
    }

    [Fact]
    public async Task ShortStatusResponses_AreParsedWithoutError()
    {
        // BVU-950P personality returns 9 status bytes instead of the documented 10.
        var (controller, _) = CreateRig(DeckPersonalities.Bvu950P);
        await using var _1 = controller;
        await controller.ConnectAsync();

        await WaitUntilAsync(() => controller.CurrentStatus.Transport == TransportState.Stopped);
    }

    // ---- Timecode polling -----------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllPersonalities))]
    public async Task TimecodePolling_DeliversLtcAndCtlAtPlaySpeed(string id)
    {
        var (controller, _) = CreateRig(Personality(id));
        await using var _1 = controller;
        await controller.ConnectAsync();
        await controller.SendTransportCommandAsync(TransportCommand.Play);

        // LTC starts at 01:00:00:00 on the simulated tape.
        await WaitUntilAsync(() => controller.CurrentTime.Ltc is { Hours: 1 });
        await WaitUntilAsync(() => controller.CurrentTime.Ctl is not null);
        Assert.Equal(TimecodeSource.Ltc, controller.CurrentTime.PrimarySource);
    }

    [Theory]
    [MemberData(nameof(AllPersonalities))]
    public async Task TimecodeAdvancesDuringPlayback(string id)
    {
        var (controller, _) = CreateRig(Personality(id));
        await using var _1 = controller;
        await controller.ConnectAsync();
        await controller.SendTransportCommandAsync(TransportCommand.Play);

        await WaitUntilAsync(() => controller.CurrentTime.Ltc is not null);
        var first = controller.CurrentTime.Ltc!.Value.ToFrameCount(25);

        await WaitUntilAsync(() =>
            controller.CurrentTime.Ltc is { } tc && tc.ToFrameCount(25) > first);
    }

    [Theory]
    [MemberData(nameof(AllPersonalities))]
    public async Task UserBits_ArePolled(string id)
    {
        var (controller, _) = CreateRig(Personality(id));
        await using var _1 = controller;
        await controller.ConnectAsync();

        await WaitUntilAsync(() => controller.CurrentTime.LtcUserBits is not null);
        Assert.Equal("20 26 01 01", controller.CurrentTime.LtcUserBits!.Value.ToString());
    }

    [Fact]
    public async Task VitcDeck_ReportsHoldVitcWhenStopped()
    {
        var (controller, _) = CreateRig(DeckPersonalities.Pvw2600P);
        await using var _1 = controller;
        await controller.ConnectAsync();

        await WaitUntilAsync(() => controller.CurrentTime.Vitc is not null);
        Assert.Equal(TimecodeSource.HoldVitc, controller.CurrentTime.PrimarySource);
    }

    [Fact]
    public async Task NonVitcDeck_ReportsCorrectedLtcWhenStopped()
    {
        var (controller, _) = CreateRig(DeckPersonalities.Bvu950P);
        await using var _1 = controller;
        await controller.ConnectAsync();

        await WaitUntilAsync(() => controller.CurrentTime.Ltc is not null);
        Assert.Equal(TimecodeSource.CorrectedLtc, controller.CurrentTime.PrimarySource);
        Assert.Null(controller.CurrentTime.Vitc);
    }

    // ---- Fault tolerance ---------------------------------------------------------------

    [Fact]
    public async Task DroppedResponses_AreRecoveredByRetry()
    {
        var flaky = DeckPersonalities.Pvw2600P with
        {
            Id = "flaky-test-deck",
            DropFirstNCommands = 1, // swallow the Device Type Request once
        };
        var (controller, _) = CreateRig(flaky, ProfileFor("sony-pvw-2600p"));
        await using var _1 = controller;

        // The transceiver's timeout+retry must absorb the dropped response.
        await controller.ConnectAsync();
        Assert.True(controller.IsConnected);

        await controller.SendTransportCommandAsync(TransportCommand.Play);
        await WaitUntilAsync(() => controller.CurrentStatus.Transport == TransportState.Playing);
    }

    [Theory]
    [MemberData(nameof(AllPersonalities))]
    public async Task Disconnect_StopsPollingAndClosesPort(string id)
    {
        var (controller, deck) = CreateRig(Personality(id));
        await using var _ = controller;
        await controller.ConnectAsync();
        await WaitUntilAsync(() => controller.CurrentStatus.Transport == TransportState.Stopped);

        await controller.DisconnectAsync();

        Assert.False(controller.IsConnected);
        Assert.False(deck.IsOpen);
        Assert.False(controller.CurrentStatus.IsConnected);
    }
}
