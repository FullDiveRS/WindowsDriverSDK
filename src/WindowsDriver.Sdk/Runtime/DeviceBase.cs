namespace WindowsDriver.Sdk;

/// <summary>
/// Base class for a device instance implemented by a driver. Subclass it and
/// override the callbacks you need — the order and semantics mirror the WDF
/// device callbacks:
///
/// <list type="bullet">
/// <item><see cref="OnPrepareHardwareAsync"/> — EvtDevicePrepareHardware: map resources, allocate HW-visible state.</item>
/// <item><see cref="OnD0EntryAsync"/> — EvtDeviceD0Entry: start the hardware (streams, timers).</item>
/// <item><see cref="OnD0ExitAsync"/> — EvtDeviceD0Exit: stop the hardware.</item>
/// <item><see cref="OnReleaseHardwareAsync"/> — EvtDeviceReleaseHardware: free HW resources.</item>
/// </list>
///
/// Queues are created with <see cref="CreateQueue"/> during OnDeviceAdd; the
/// default queue is already created for you.
/// </summary>
public abstract class DeviceBase
{
    private readonly List<IoQueue> _queues = [];

    protected DeviceBase(DeviceContext context)
    {
        Context = context;
        DefaultQueue = CreateQueue(new IoQueueOptions { Name = "default", Dispatch = QueueDispatchMode.Parallel });
    }

    /// <summary>Context for this device instance (ids, properties, interface creation).</summary>
    public DeviceContext Context { get; }

    /// <summary>Convenience accessor for the driver context.</summary>
    public DriverContext Driver => Context.Driver;

    /// <summary>The automatically created default queue (parallel dispatch).</summary>
    public IoQueue DefaultQueue { get; }

    public IReadOnlyList<IoQueue> Queues => _queues;

    /// <summary>True once OnD0EntryAsync completed successfully (running = powered).</summary>
    public bool IsD0 { get; private set; }

    /// <summary>True once OnPrepareHardwareAsync completed successfully.</summary>
    public bool IsHardwarePrepared { get; private set; }

    /// <summary>Creates an additional queue (WdfIoQueueCreate).</summary>
    protected IoQueue CreateQueue(IoQueueOptions options)
    {
        var queue = new IoQueue(Context.Driver.Log, options);
        _queues.Add(queue);
        return queue;
    }

    // ----- Power/PnP lifecycle -----

    public virtual Task OnPrepareHardwareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnReleaseHardwareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnD0EntryAsync(DevicePowerState previousState, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task OnD0ExitAsync(DevicePowerState newState, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Approve removal by returning normally; deny it by throwing
    /// <see cref="DriverException"/> with a failure status (EvtDeviceQueryRemove).
    /// </summary>
    public virtual Task OnQueryRemoveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Device vanished without warning (unplug). Stop touching the hardware.</summary>
    public virtual Task OnSurpriseRemovalAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ----- Internals (hosts call these wrappers so IsD0/IsHardwarePrepared stay accurate) -----

    /// <summary>Host entry point: runs <see cref="OnPrepareHardwareAsync"/> and marks hardware prepared.</summary>
    public async Task PrepareHardwareAsync(CancellationToken ct)
    {
        await OnPrepareHardwareAsync(ct).ConfigureAwait(false);
        IsHardwarePrepared = true;
    }

    /// <summary>Host entry point: runs <see cref="OnReleaseHardwareAsync"/> and clears the prepared flag.</summary>
    public async Task ReleaseHardwareAsync(CancellationToken ct)
    {
        await OnReleaseHardwareAsync(ct).ConfigureAwait(false);
        IsHardwarePrepared = false;
    }

    /// <summary>Host entry point: runs <see cref="OnD0EntryAsync"/> and marks the device powered.</summary>
    public async Task D0EntryAsync(DevicePowerState previous, CancellationToken ct)
    {
        await OnD0EntryAsync(previous, ct).ConfigureAwait(false);
        IsD0 = true;
    }

    /// <summary>Host entry point: runs <see cref="OnD0ExitAsync"/> and clears the powered flag.</summary>
    public async Task D0ExitAsync(DevicePowerState next, CancellationToken ct)
    {
        await OnD0ExitAsync(next, ct).ConfigureAwait(false);
        IsD0 = false;
    }

    /// <summary>Stops all queues; pending requests are cancelled (queue purge semantics).</summary>
    public async Task StopQueuesAsync()
    {
        foreach (var queue in _queues)
        {
            await queue.StopAsync().ConfigureAwait(false);
        }
    }
}
