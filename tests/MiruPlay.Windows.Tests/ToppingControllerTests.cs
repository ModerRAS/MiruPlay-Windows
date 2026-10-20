using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class ToppingControllerTests
{
    private static readonly ToppingHidDevice Dx5II = new("hid-path", "TOPPING DX5 II", ToppingProtocol.Dx5II);
    private static readonly ToppingHidDevice D90III = new("hid-path", "Topping D90III Discrete", ToppingProtocol.D90III);

    [Fact]
    public void StatusReportsUnattachedWithoutHardware()
    {
        var status = new ToppingController(new MockTransport([])).GetStatus();

        Assert.False(status.Attached);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public async Task SetVolumeWritesOneFramePlusCommitAndCaches()
    {
        var transport = new MockTransport([Dx5II]);
        var controller = new ToppingController(transport);

        var status = await controller.SetVolumeAsync(-20);

        Assert.Equal(2, transport.Sends.Count); // Volume frame + commit.
        Assert.Equal(ToppingProtocol.SubVolume, transport.Sends[0][6]);
        Assert.Equal(ToppingProtocol.SubCommit, transport.Sends[1][6]);
        Assert.Equal(-20, status.VolumeDb);
        Assert.Equal("Topping DX5 II", status.Model);
    }

    [Fact]
    public async Task LoudVolumeRequiresExplicitConfirmation()
    {
        var controller = new ToppingController(new MockTransport([Dx5II]));

        var error = await Assert.ThrowsAsync<ToppingController.ToppingCommandException>(
            () => controller.SetVolumeAsync(-5));
        Assert.Contains("二次确认", error.Message);

        var status = await controller.SetVolumeAsync(-5, confirmed: true);
        Assert.Equal(-5, status.VolumeDb);
    }

    [Fact]
    public async Task VolumeOutsideRangeIsRejectedBeforeAnyWrite()
    {
        var transport = new MockTransport([Dx5II]);
        var controller = new ToppingController(transport);

        await Assert.ThrowsAsync<ToppingController.ToppingCommandException>(() => controller.SetVolumeAsync(1));
        Assert.Empty(transport.Sends);
    }

    [Fact]
    public async Task UnconfirmedModelsAreReadOnlyEvenWhenRecognized()
    {
        var transport = new MockTransport([D90III]);
        var controller = new ToppingController(transport);

        var status = controller.GetStatus();
        Assert.True(status.Attached);
        Assert.False(status.Confirmed);
        var error = await Assert.ThrowsAsync<ToppingController.ToppingCommandException>(
            () => controller.SetGainAsync(true));
        Assert.Contains("未在实机确认", error.Message);
        Assert.Empty(transport.Sends);
    }

    [Fact]
    public async Task KnobOnlyVolumeIsRefusedOnConfirmedModels()
    {
        var knobOnly = Dx5II with { Spec = Dx5II.Spec with { HidVolume = false } };
        var controller = new ToppingController(new MockTransport([knobOnly]));

        var error = await Assert.ThrowsAsync<ToppingController.ToppingCommandException>(
            () => controller.SetVolumeAsync(-20));
        Assert.Contains("不在 USB HID 接口上", error.Message);
    }

    [Fact]
    public async Task PowerSendsAChecksummedFrameWithoutCommit()
    {
        var transport = new MockTransport([Dx5II]);
        var controller = new ToppingController(transport);

        await controller.SetPowerAsync(true);

        var send = Assert.Single(transport.Sends);
        Assert.Equal(ToppingProtocol.SubPower, send[6]);
        Assert.NotEqual(0, (send[11] << 8) | send[12]);
    }

    [Fact]
    public async Task FlatDisablesAllElevenRegisters()
    {
        var transport = new MockTransport([Dx5II]);
        var controller = new ToppingController(transport);

        var status = await controller.FlatAsync();

        Assert.Equal(11 * 10 + 1, transport.Sends.Count); // 11 bands x 10 frames + commit.
        Assert.Equal(11, status.Bands?.Count);
        Assert.All(status.Bands!, band => Assert.False(band.On));
    }

    [Fact]
    public async Task ApplyPresetPushesBandsAndCachesWriteState()
    {
        var transport = new MockTransport([Dx5II]);
        var controller = new ToppingController(transport);
        var preset = new AudioDspPreset(
            "test", "Test",
            Rules: [new AudioDspChannelRule(Bands: [new AudioDspBand(AudioDspFilterType.Peaking, 1_000, -3, 1)])]);

        var mapping = await controller.ApplyPresetAsync(preset);

        Assert.Empty(mapping.Warnings);
        Assert.Equal(11 * 10 + 1, transport.Sends.Count); // Zero preamp: bands + commit only.
        var status = controller.GetStatus();
        Assert.True(status.Bands?[0].On);
        Assert.Null(status.PreampDb);
    }

    [Fact]
    public async Task ApplyPresetWithoutUsableBandsIsRefused()
    {
        var transport = new MockTransport([Dx5II]);
        var controller = new ToppingController(transport);
        var preset = new AudioDspPreset(
            "test", "Test",
            Rules: [new AudioDspChannelRule(Bands: [new AudioDspBand(AudioDspFilterType.LowPass, 200)])]);

        await Assert.ThrowsAsync<ToppingController.ToppingCommandException>(
            () => controller.ApplyPresetAsync(preset));
        Assert.Empty(transport.Sends);
    }

    private sealed class MockTransport(IReadOnlyList<ToppingHidDevice> devices) : IToppingHidTransport
    {
        public List<byte[]> Sends { get; } = [];
        public IReadOnlyList<ToppingHidDevice> ListDevices() => devices;
        public void SendFrame(ToppingHidDevice device, byte[] wireBytes) => Sends.Add(wireBytes.ToArray());
    }
}
