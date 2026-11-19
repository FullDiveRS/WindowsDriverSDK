namespace WindowsDriver.Sdk;

/// <summary>A device instance managed by <see cref="DriverHost"/>.</summary>
public sealed class DeviceInstance
{
    internal DeviceInstance(DeviceBase device, DeviceContext context)
    {
        Device = device;
        Context = context;
    }

    public DeviceBase Device { get; }
    public DeviceContext Context { get; }

    public string DeviceInstanceId => Context.DeviceInstanceId;
    public IoQueue DefaultQueue => Device.DefaultQueue;
}

/// <summary>
/// Orchestrates the driver and device lifecycle exactly like the WDF framework
/// would: DriverEntry → AddDevice → PrepareHardware → D0Entry, and the mirror
/// image on shutdown. The UMDF shim, the KMDF control plane and the test host
/// all drive this class — so behavior you verify in tests is the behavior you
/// get in production.
/// </summary>
public sealed class DriverHost
{
    private readonly List<DeviceInstance> _devices = [];
    private readonly object _gate = new();
    private IDriver? _driver;
    private DriverContext? _driverContext;

    public DriverHost(IDriverLog? log = null, IDriverRegistry? registry = null, TimeProvider? timeProvider = null)
    {
        Log = log ?? ConsoleDriverLog.Instance;
        Registry = registry ?? new MemoryRegistry();
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public IDriverLog Log { get; }
    public IDriverRegistry Registry { get; }
    public TimeProvider TimeProvider { get; }
    public bool IsStarted { get; private set; }

    public IReadOnlyList<DeviceInstance> Devices
    {
        get { lock (_gate) { return _devices.ToArray(); } }
    }

    /// <summary>Loads the driver type (must implement <see cref="IDriver"/>, parameterless ctor) and runs DriverEntry.</summary>
    public async Task StartAsync(Type driverType, string? serviceName = null, CancellationToken cancellationToken = default)
    {
        if (IsStarted)
        {
            throw new InvalidOperationException("DriverHost is already started.");
        }
        var driver = (IDriver)Activator.CreateInstance(driverType)!;
        var context = new DriverContext
        {
            Log = Log,
            Registry = Registry,
            TimeProvider = TimeProvider,
            DriverServiceName = serviceName ?? driverType.Name,
        };
        await driver.OnDriverEntryAsync(context, cancellationToken).ConfigureAwait(false);
        _driver = driver;
        _driverContext = context;
        IsStarted = true;
        Log.Info("driver {0} loaded (service '{1}')", driverType.Name, context.DriverServiceName);
    }

    public Task StartAsync<TDriver>(string? serviceName = null, CancellationToken cancellationToken = default) where TDriver : IDriver, new()
        => StartAsync(typeof(TDriver), serviceName, cancellationToken);

    /// <summary>
    /// Adds a device instance and powers it up: AddDevice → PrepareHardware → D0Entry.
    /// Throws on failure (after rolling back partially completed steps).
    /// </summary>
    public async Task<DeviceInstance> AddDeviceAsync(DeviceAddContext addContext, CancellationToken cancellationToken = default)
    {
        if (!IsStarted || _driver is null)
        {
            throw new InvalidOperationException("Call StartAsync before AddDeviceAsync.");
        }

        var context = new DeviceContext(_driverContext!, addContext);

        var device = await _driver.OnDeviceAddAsync(addContext, context, cancellationToken).ConfigureAwait(false)
                     ?? throw new DriverException(NtStatus.Unsuccessful, "OnDeviceAddAsync returned null.");

        try
        {
            await device.PrepareHardwareAsync(cancellationToken).ConfigureAwait(false);
            await device.D0EntryAsync(DevicePowerState.D3, cancellationToken).ConfigureAwait(false);
        }
        catch (DriverException)
        {
            await RollbackAsync(device, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(device, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("device add failed unexpectedly: {0}", ex);
            await RollbackAsync(device, cancellationToken).ConfigureAwait(false);
            throw new DriverException(NtStatus.Unsuccessful, "Device add failed.", ex);
        }

        var instance = new DeviceInstance(device, context);
        lock (_gate)
        {
            _devices.Add(instance);
        }
        Log.Info("device {0} added and started (interfaces: {1})", addContext.DeviceInstanceId, context.DeviceInterfaces.Count);
        return instance;
    }

    private async Task RollbackAsync(DeviceBase device, CancellationToken ct)
    {
        try
        {
            await device.StopQueuesAsync().ConfigureAwait(false);
            if (device.IsD0)
            {
                await device.D0ExitAsync(DevicePowerState.D3, ct).ConfigureAwait(false);
            }
            if (device.IsHardwarePrepared)
            {
                await device.ReleaseHardwareAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("rollback during failed device add also failed: {0}", ex.Message);
        }
    }

    /// <summary>Powers down and releases one device (QueryRemove honored by the device callbacks).</summary>
    public async Task RemoveDeviceAsync(DeviceInstance instance, CancellationToken cancellationToken = default)
    {
        await instance.Device.OnQueryRemoveAsync(cancellationToken).ConfigureAwait(false);
        await instance.Device.D0ExitAsync(DevicePowerState.D3, cancellationToken).ConfigureAwait(false);
        await instance.Device.StopQueuesAsync().ConfigureAwait(false);
        await instance.Device.ReleaseHardwareAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _devices.Remove(instance);
        }
        Log.Info("device {0} removed", instance.DeviceInstanceId);
    }

    /// <summary>Notifies the device of an unannounced removal (unplug).</summary>
    public async Task SurpriseRemoveDeviceAsync(DeviceInstance instance, CancellationToken cancellationToken = default)
    {
        await instance.Device.OnSurpriseRemovalAsync(cancellationToken).ConfigureAwait(false);
        await instance.Device.StopQueuesAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _devices.Remove(instance);
        }
    }

    /// <summary>Powers down all devices and calls DriverUnload.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_driver is null)
        {
            return;
        }
        DeviceInstance[] devices;
        lock (_gate)
        {
            devices = _devices.ToArray();
            _devices.Clear();
        }
        foreach (var device in devices)
        {
            try
            {
                await device.Device.D0ExitAsync(DevicePowerState.D3, cancellationToken).ConfigureAwait(false);
                await device.Device.StopQueuesAsync().ConfigureAwait(false);
                await device.Device.ReleaseHardwareAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning("error during shutdown of {0}: {1}", device.DeviceInstanceId, ex.Message);
            }
        }
        await _driver.OnDriverUnloadAsync().ConfigureAwait(false);
        IsStarted = false;
        _driver = null;
        Log.Info("driver unloaded");
    }
}
