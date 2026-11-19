using WindowsDriver.Sdk;

namespace WindowsDriver.Testing;

/// <summary>
/// A complete in-memory environment for running driver code without Windows:
/// fake clock, captured log, fake registry and a virtual device bus. The same
/// <see cref="DriverHost"/> orchestration used by the production shims drives
/// lifecycle, so verified behavior transfers to deployment.
/// </summary>
public sealed class TestDriverHost
{
    internal TestDriverHost(DriverHost host, CapturedLog log, FakeRegistry registry, FakeTimeProvider? time)
    {
        Host = host;
        Log = log;
        Registry = registry;
        FakeTime = time;
    }

    public DriverHost Host { get; }
    public CapturedLog Log { get; }
    public FakeRegistry Registry { get; }

    /// <summary>The fake clock, when the builder was configured with one; null with real time.</summary>
    public FakeTimeProvider? FakeTime { get; }

    public IReadOnlyList<TestDevice> Devices =>
        Host.Devices.Select(d => new TestDevice(d)).ToArray();

    public bool IsStarted => Host.IsStarted;

    public Task StartAsync<TDriver>(string? serviceName = null, CancellationToken ct = default) where TDriver : IDriver, new()
        => Host.StartAsync(typeof(TDriver), serviceName, ct);

    public Task StartAsync(Type driverType, string? serviceName = null, CancellationToken ct = default)
        => Host.StartAsync(driverType, serviceName, ct);

    public Task<TestDevice> AddDeviceAsync(DeviceAddContext addContext, CancellationToken ct = default)
        => Host.AddDeviceAsync(addContext, ct).ContinueWith(t => new TestDevice(t.Result), ct);

    /// <summary>Adds a device with just an instance id (hardware ids inferred).</summary>
    public async Task<TestDevice> AddDeviceAsync(string deviceInstanceId, params string[] hardwareIds)
    {
        var instance = await Host.AddDeviceAsync(new DeviceAddContext
        {
            DeviceInstanceId = deviceInstanceId,
            HardwareIds = hardwareIds,
        });
        return new TestDevice(instance);
    }

    public Task ShutdownAsync(CancellationToken ct = default) => Host.ShutdownAsync(ct);

    public static TestDriverHostBuilder Builder() => new();
}

/// <summary>Fluent configuration for <see cref="TestDriverHost"/>.</summary>
public sealed class TestDriverHostBuilder
{
    private CapturedLog? _log;
    private FakeRegistry? _registry;
    private FakeTimeProvider? _timeProvider;

    /// <summary>Sets a fake clock starting at the given time.</summary>
    public TestDriverHostBuilder UseFakeTime(DateTimeOffset? start = null)
    {
        _timeProvider = new FakeTimeProvider(start);
        return this;
    }

    /// <summary>Uses real wall-clock time (rarely wanted in tests).</summary>
    public TestDriverHostBuilder UseRealTime()
    {
        _timeProvider = null;
        return this;
    }

    public TestDriverHostBuilder WithRegistry(FakeRegistry registry)
    {
        _registry = registry;
        return this;
    }

    /// <summary>Sets the minimum level for the captured log (default Trace).</summary>
    public TestDriverHostBuilder WithLogMinimum(DriverLogLevel minimum)
    {
        _log = new CapturedLog(minimum);
        return this;
    }

    public TestDriverHost Build()
    {
        var log = _log ?? new CapturedLog();
        var registry = _registry ?? new FakeRegistry();
        var host = new DriverHost(log, registry, _timeProvider);
        return new TestDriverHost(host, log, registry, _timeProvider);
    }
}

/// <summary>
/// A device under test: wraps the <see cref="DeviceInstance"/> and offers typed
/// helpers for sending IOCTLs, reading and writing — as an application or the
/// WDF queue would.
/// </summary>
public sealed class TestDevice
{
    private int _nextRequestId = 1;

    internal TestDevice(DeviceInstance instance) => Instance = instance;

    public DeviceInstance Instance { get; }
    public DeviceBase Device => Instance.Device;
    public DeviceContext Context => Instance.Context;
    public string DeviceInstanceId => Instance.DeviceInstanceId;
    public IoQueue DefaultQueue => Instance.DefaultQueue;

    /// <summary>Sends an IOCTL and awaits its completion. Output buffer defaults to 256 bytes.</summary>
    public Task<IoResult> SendIoctlAsync(uint controlCode, byte[]? input = null, int outputSize = 256, TimeSpan? timeout = null)
    {
        var request = new IoRequest(
            IoRequestKind.DeviceControl,
            controlCode,
            input ?? Array.Empty<byte>(),
            new byte[outputSize],
            (ulong)Interlocked.Increment(ref _nextRequestId));
        return SubmitAsync(request, timeout);
    }

    /// <summary>Serializes the request payload (little-endian) and sends it as an IOCTL input.</summary>
    public Task<IoResult> SendIoctlAsync<TPayload>(uint controlCode, TPayload payload, Func<TPayload, byte[]> serializer, int outputSize = 256)
        => SendIoctlAsync(controlCode, serializer(payload), outputSize);

    public Task<IoResult> ReadAsync(int bytes = 256, TimeSpan? timeout = null)
    {
        var request = new IoRequest(IoRequestKind.Read, 0, Array.Empty<byte>(), new byte[bytes],
            (ulong)Interlocked.Increment(ref _nextRequestId));
        return SubmitAsync(request, timeout);
    }

    public Task<IoResult> WriteAsync(byte[] data, TimeSpan? timeout = null)
    {
        var request = new IoRequest(IoRequestKind.Write, 0, data, Array.Empty<byte>(),
            (ulong)Interlocked.Increment(ref _nextRequestId));
        return SubmitAsync(request, timeout);
    }

    private async Task<IoResult> SubmitAsync(IoRequest request, TimeSpan? timeout)
    {
        var completion = new TaskCompletionSource<IoRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        request.Completed += completion.SetResult;
        DefaultQueue.Submit(request);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        var finished = await Task.WhenAny(completion.Task, Task.Delay(effectiveTimeout));
        if (finished != completion.Task)
        {
            throw new TimeoutException($"Request {request.Kind} (ioctl 0x{request.IoControlCode:X8}) did not complete within {effectiveTimeout.TotalSeconds}s.");
        }
        return IoResult.From(request);
    }
}
