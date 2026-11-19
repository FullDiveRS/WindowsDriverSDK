using System.Text;
using System.Threading.Channels;

namespace WindowsDriver.Sdk.Sensor;

/// <summary>
/// SDK-level IOCTL contract for sensor devices handled by <see cref="SensorDevice"/>.
/// Payload layouts are implemented with <see cref="WireFormat"/>; see
/// docs/sensor-guide.md for the SensorCx mapping.
/// </summary>
public static class SensorIoctlCodes
{
    private static uint Ctl(uint function, uint access) => WinIoctl.VendorControlCode(function, access);

    /// <summary>Out: u32 sensor count.</summary>
    public static readonly uint GetSensorCount = Ctl(0x800, WinIoctl.FileAccessRead);

    /// <summary>In: u32 index. Out: (u32 kind, u32 valueCount, u32 sampleRateMilliHz, char[16] unit).</summary>
    public static readonly uint GetSensorInfo = Ctl(0x801, WinIoctl.FileAccessRead);

    /// <summary>In: u32 index. Out: (u64 timestampTicks100ns, u32 valueCount, valueCount × f64).</summary>
    public static readonly uint GetLatestReading = Ctl(0x802, WinIoctl.FileAccessRead);

    /// <summary>In: (u32 index, u32 sampleRateMilliHz). Sets the poll rate.</summary>
    public static readonly uint SetSampleRate = Ctl(0x803, WinIoctl.FileAccessWrite);
}

/// <summary>Configuration for <see cref="SensorDevice"/>.</summary>
public sealed class SensorDeviceOptions
{
    /// <summary>The sensors this device publishes (order defines indices).</summary>
    public IReadOnlyList<ISensorSource> Sources { get; init; } = [];

    /// <summary>Background sampling interval cap: real drivers sample on hardware interrupts; the loop uses at most this many ticks/second.</summary>
    public bool RunBackgroundSamplingLoop { get; init; } = true;

    /// <summary>Maximum readings buffered in the event queue before old ones are dropped.</summary>
    public int EventQueueCapacity { get; init; } = 256;
}

/// <summary>
/// A virtual sensor device hosting one or more <see cref="ISensorSource"/>s.
/// Clients poll <see cref="SensorIoctlCodes.GetLatestReading"/> or subscribe via
/// the event queue (<see cref="Readings"/>). A background loop samples at each
/// sensor's rate while the device is in D0; <see cref="SampleOnce"/> also works
/// synchronously for tests.
/// </summary>
public class SensorDevice : DeviceBase
{
    private readonly SensorDeviceOptions _options;
    private readonly ISensorSource[] _sources;
    private readonly SensorReading?[] _latest;
    private readonly Channel<SensorReading> _readings;
    private Task? _samplingLoop;
    private CancellationTokenSource? _loopCancellation;

    public SensorDevice(DeviceContext context, SensorDeviceOptions? options = null)
        : base(context)
    {
        _options = options ?? new SensorDeviceOptions();
        _sources = _options.Sources.ToArray();
        if (_sources.Length == 0)
        {
            throw new ArgumentException("At least one sensor source is required.", nameof(options));
        }
        _latest = new SensorReading?[_sources.Length];
        _readings = Channel.CreateBounded<SensorReading>(new BoundedChannelOptions(_options.EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        DefaultQueue
            .OnDeviceControl(HandleDeviceControlAsync)
            .OnRead(HandleReadAsync);
    }

    /// <summary>Event queue of sampled readings (dropped-oldest when full).</summary>
    public ChannelReader<SensorReading> Readings => _readings.Reader;

    public int SensorCount => _sources.Length;

    public ISensorSource GetSource(int index) => _sources[index];

    /// <summary>Samples every sensor once (background loop calls this too).</summary>
    public void SampleOnce()
    {
        var now = Driver.TimeProvider.GetUtcNow();
        for (var i = 0; i < _sources.Length; i++)
        {
            var reading = _sources[i].Sample(now);
            _latest[i] = reading;
            _readings.Writer.TryWrite(reading);
        }
    }

    public SensorReading? LatestReading(int index) => _latest[index];

    public override Task OnD0EntryAsync(DevicePowerState previousState, CancellationToken cancellationToken)
    {
        if (_options.RunBackgroundSamplingLoop)
        {
            _loopCancellation = new CancellationTokenSource();
            _samplingLoop = Task.Run(() => SamplingLoopAsync(_loopCancellation.Token));
        }
        return Task.CompletedTask;
    }

    public override async Task OnD0ExitAsync(DevicePowerState newState, CancellationToken cancellationToken)
    {
        if (_loopCancellation is not null)
        {
            _loopCancellation.Cancel();
            try
            {
                if (_samplingLoop is not null)
                {
                    await _samplingLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                Context.Log.Warning("sensor sampling loop did not stop within 2s");
            }
            _loopCancellation.Dispose();
            _loopCancellation = null;
            _samplingLoop = null;
        }
    }

    private async Task SamplingLoopAsync(CancellationToken ct)
    {
        // Interleave sensors: the loop wakes at the fastest configured rate and
        // samples everything (cheap for simulated sources).
        var fastestHz = _sources.Max(s => s.SampleRateHz);
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, 1000.0 / fastestHz));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, Driver.TimeProvider, ct).ConfigureAwait(false);
                SampleOnce();
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private Task HandleReadAsync(IoRequest request, CancellationToken ct)
    {
        // Read = latest accelerometer values, if present (convenience for stream-style clients).
        var accel = Array.FindIndex(_sources, s => s.Kind == SensorKind.Accelerometer3D);
        if (accel < 0 || _latest[accel] is not { } reading)
        {
            request.Fail(NtStatus.DeviceNotReady);
            return Task.CompletedTask;
        }
        var needed = sizeof(ulong) + sizeof(uint) + reading.Values.Count * sizeof(double);
        if (request.OutputBuffer.Length < needed)
        {
            request.Fail(NtStatus.BufferTooSmall);
            return Task.CompletedTask;
        }
        WriteReading(request.OutputBuffer.Span, reading);
        request.Complete(NtStatus.Success, needed);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the <see cref="SensorIoctlCodes"/> contract. Subclasses override
    /// this to add device-specific IOCTLs and fall back to base for the rest.
    /// </summary>
    protected virtual Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        var code = request.IoControlCode;
        if (code == SensorIoctlCodes.GetSensorCount)
        {
            if (request.OutputBuffer.Length < sizeof(uint))
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                WireFormat.WriteUInt32(request.OutputBuffer.Span, 0, (uint)_sources.Length);
                request.Complete(NtStatus.Success, sizeof(uint));
            }
        }
        else if (code == SensorIoctlCodes.GetSensorInfo)
        {
            var index = ReadIndex(request, _sources.Length);
            var source = _sources[index];
            var needed = 3 * sizeof(uint) + 16;
            if (request.OutputBuffer.Length < needed)
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                var span = request.OutputBuffer.Span;
                WireFormat.WriteUInt32(span, 0, (uint)source.Kind);
                WireFormat.WriteUInt32(span, 4, (uint)source.ValueCount);
                WireFormat.WriteUInt32(span, 8, (uint)(source.SampleRateHz * 1000));
                var unit = Encoding.ASCII.GetBytes(source.Unit);
                span.Slice(12, 16).Fill(0);
                unit.AsSpan(0, Math.Min(unit.Length, 16)).CopyTo(span.Slice(12));
                request.Complete(NtStatus.Success, needed);
            }
        }
        else if (code == SensorIoctlCodes.GetLatestReading)
        {
            var index = ReadIndex(request, _sources.Length);
            if (_latest[index] is not { } reading)
            {
                request.Fail(NtStatus.DeviceNotReady);
            }
            else
            {
                var needed = sizeof(ulong) + sizeof(uint) + reading.Values.Count * sizeof(double);
                if (request.OutputBuffer.Length < needed)
                {
                    request.Fail(NtStatus.BufferTooSmall);
                }
                else
                {
                    WriteReading(request.OutputBuffer.Span, reading);
                    request.Complete(NtStatus.Success, needed);
                }
            }
        }
        else if (code == SensorIoctlCodes.SetSampleRate)
        {
            if (request.InputBuffer.Length < 2 * sizeof(uint))
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                var index = (int)WireFormat.ReadUInt32(request.InputBuffer.Span, 0);
                if (index < 0 || index >= _sources.Length)
                {
                    request.Fail(NtStatus.InvalidParameter);
                }
                else
                {
                    var milliHz = WireFormat.ReadUInt32(request.InputBuffer.Span, 4);
                    _sources[index].SampleRateHz = milliHz / 1000.0;
                    request.Complete(NtStatus.Success);
                }
            }
        }
        else
        {
            request.Fail(NtStatus.InvalidDeviceRequest);
        }
        return Task.CompletedTask;
    }

    private static int ReadIndex(IoRequest request, int sensorCount)
    {
        if (request.InputBuffer.Length < sizeof(uint))
        {
            throw new DriverException(NtStatus.BufferTooSmall, "Sensor index buffer too small.");
        }
        var index = (int)WireFormat.ReadUInt32(request.InputBuffer.Span, 0);
        if (index < 0 || index >= sensorCount)
        {
            throw new DriverException(NtStatus.InvalidParameter, $"Sensor index {index} is out of range (0..{sensorCount - 1}).");
        }
        return index;
    }

    private static void WriteReading(Span<byte> dest, SensorReading reading)
    {
        WireFormat.WriteUInt64(dest, 0, (ulong)reading.Timestamp.UtcTicks);
        WireFormat.WriteUInt32(dest, 8, (uint)reading.Values.Count);
        for (var i = 0; i < reading.Values.Count; i++)
        {
            WireFormat.WriteDouble(dest, 12 + i * sizeof(double), reading.Values[i]);
        }
    }
}
