namespace MiruPlay.Windows.Services;

/// <summary>
/// High-level Topping DAC control: volume, gain, power, PEQ preset push, flat, preamp.
/// State is a write-cache only — the DAC cannot be queried over this interface, so
/// anything changed from the front panel/remote/vendor app is invisible here.
/// Writes are refused unless the model was hardware-confirmed (shared PID models
/// may have colliding register maps).
/// </summary>
public sealed class ToppingController
{
    public sealed record Status(
        bool Attached,
        string? Model,
        string? DeviceKey,
        bool Confirmed,
        bool HidVolume,
        string? Reason = null,
        IReadOnlyList<ToppingProtocol.PeqBand>? Bands = null,
        double? PreampDb = null,
        double? VolumeDb = null,
        bool? GainOn = null);

    public sealed class ToppingCommandException(string message) : Exception(message);

    private readonly IToppingHidTransport _transport;
    private readonly object _sync = new();
    private Status? _lastState;

    public ToppingController(IToppingHidTransport? transport = null) =>
        _transport = transport ?? new WindowsHidTransport();

    public Status GetStatus()
    {
        var attached = _transport.ListDevices();
        if (attached.Count == 0)
            return new Status(false, null, null, false, false, AppStrings.Get("top.noDevice"));
        var device = attached[0];
        var cached = Volatile.Read(ref _lastState);
        return new Status(
            true, device.Spec.Name, device.Spec.Key,
            device.Spec.Confirmed, device.Spec.HidVolume,
            null,
            cached?.Bands, cached?.PreampDb, cached?.VolumeDb, cached?.GainOn);
    }

    public async Task<Status> SetVolumeAsync(double db, bool confirmed = false, double stepDb = 0.5, CancellationToken cancellationToken = default)
    {
        if (db is < ToppingProtocol.VolumeMinDb or > ToppingProtocol.VolumeMaxDb)
            throw new ToppingCommandException(AppStrings.Get("top.volRange", db));
        if (db > ToppingProtocol.VolumeWarnDb && !confirmed)
            throw new ToppingCommandException(AppStrings.Get("top.loudConfirm", db));
        var device = Resolve();
        if (!device.Spec.HidVolume)
            throw new ToppingCommandException(AppStrings.Get("top.noHidVolume", device.Spec.Name));
        await SendAllAsync(device, ToppingProtocol.VolumeFrames(db, stepDb), cancellationToken).ConfigureAwait(false);
        return Remember(status => status with { VolumeDb = db });
    }

    public async Task<Status> SetGainAsync(bool on, CancellationToken cancellationToken = default)
    {
        var device = Resolve();
        await SendAllAsync(device, [ToppingProtocol.GainFrame(on)], cancellationToken).ConfigureAwait(false);
        return Remember(status => status with { GainOn = on });
    }

    public async Task<Status> SetPowerAsync(bool on, CancellationToken cancellationToken = default)
    {
        var device = Resolve();
        // Power answers the checksum oracle: no commit frame after it.
        _transport.SendFrame(device, ToppingProtocol.WireBytes(device.Spec, ToppingProtocol.PowerFrame(on)));
        await Task.CompletedTask.ConfigureAwait(false);
        return GetStatus();
    }

    /// <summary>Push an in-app preset's PEQ bands + preamp to the DAC hardware.</summary>
    public async Task<ToppingProtocol.PeqMapping> ApplyPresetAsync(
        Models.AudioDspPreset preset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var device = Resolve();
        var mapping = ToppingProtocol.MapPreset(preset);
        var hasUsableBand = false;
        foreach (var band in mapping.Bands)
        {
            if (band.On)
            {
                hasUsableBand = true;
                break;
            }
        }
        if (!hasUsableBand)
            throw new ToppingCommandException(AppStrings.Get("top.noBands"));
        var frames = new List<byte[]>();
        if (mapping.PreampDb is { } preamp) frames.AddRange(ToppingProtocol.PreampFrames(preamp));
        for (var i = 0; i < mapping.Bands.Count; i++) frames.AddRange(ToppingProtocol.BandFrames(i, mapping.Bands[i]));
        await SendAllAsync(device, frames, cancellationToken).ConfigureAwait(false);
        lock (_sync) _lastState = GetStatus() with { Bands = mapping.Bands, PreampDb = mapping.PreampDb };
        return mapping;
    }

    /// <summary>Disable every band (curve bypass), preamp untouched.</summary>
    public async Task<Status> FlatAsync(CancellationToken cancellationToken = default)
    {
        var device = Resolve();
        var frames = Enumerable.Range(0, ToppingProtocol.PeqRegisterCount)
            .SelectMany(index => ToppingProtocol.BandFrames(index, ToppingProtocol.DisabledBand))
            .ToList();
        await SendAllAsync(device, frames, cancellationToken).ConfigureAwait(false);
        return Remember(status => status with
        {
            Bands = Enumerable.Repeat(ToppingProtocol.DisabledBand, ToppingProtocol.PeqRegisterCount).ToList(),
        });
    }

    public async Task<Status> SetPreampAsync(double db, CancellationToken cancellationToken = default)
    {
        if (db is < ToppingProtocol.PreampMinDb or > ToppingProtocol.PreampMaxDb)
            throw new ToppingCommandException(AppStrings.Get("top.preampRange", db));
        var device = Resolve();
        await SendAllAsync(device, ToppingProtocol.PreampFrames(db), cancellationToken).ConfigureAwait(false);
        return Remember(status => status with { PreampDb = db });
    }

    private ToppingHidDevice Resolve(string? deviceKey = null)
    {
        var attached = _transport.ListDevices();
        ToppingHidDevice? device = null;
        foreach (var candidate in attached)
        {
            if (deviceKey is null || candidate.Spec.Key == deviceKey)
            {
                device = candidate;
                break;
            }
        }
        if (device is null)
            throw new ToppingCommandException(AppStrings.Get("top.notFound", deviceKey ?? AppStrings.Get("top.noConfirmedModel")));
        if (!device.Spec.Confirmed)
            throw new ToppingCommandException(AppStrings.Get("top.unconfirmed", device.Spec.Name));
        return device;
    }

    private async Task SendAllAsync(ToppingHidDevice device, IReadOnlyList<byte[]> frames, CancellationToken cancellationToken)
    {
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _transport.SendFrame(device, ToppingProtocol.WireBytes(device.Spec, frame));
            // The vendor app paces writes; don't flood the DSP.
            await Task.Delay(4, cancellationToken).ConfigureAwait(false);
        }
        _transport.SendFrame(device, ToppingProtocol.WireBytes(device.Spec, ToppingProtocol.CommitFrame()));
    }

    private Status Remember(Func<Status, Status> update)
    {
        lock (_sync)
        {
            var next = update(GetStatus());
            _lastState = next;
            return next;
        }
    }
}
