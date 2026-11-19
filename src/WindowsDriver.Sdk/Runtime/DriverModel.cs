namespace WindowsDriver.Sdk;

/// <summary>
/// Bus-reported identity of a device instance being added — the managed
/// equivalent of the information WDF hands to EvtDriverDeviceAdd.
/// </summary>
public sealed record DeviceAddContext
{
    /// <summary>PnP device instance id, e.g. "ROOT\SAMPLEAUDIO\0000".</summary>
    public required string DeviceInstanceId { get; init; }

    /// <summary>Hardware IDs in best-match order (most specific first).</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = Array.Empty<string>();

    /// <summary>Compatible IDs, used when no hardware ID matches.</summary>
    public IReadOnlyList<string> CompatibleIds { get; init; } = Array.Empty<string>();

    /// <summary>Initial devnode property store.</summary>
    public PropertyStore Properties { get; init; } = new();

    /// <summary>True when the device was enumerated by the PnP manager as "raw" (no INF match yet).</summary>
    public bool IsRawDevice { get; init; }
}

/// <summary>
/// The driver entry point set. Write a class implementing this interface and
/// host it with <see cref="DriverHost"/>, the UMDF shim, or the test host.
/// Callback order matches WDF: DriverEntry → (AddDevice → PrepareHardware →
/// D0Entry)* → DriverUnload.
/// </summary>
public interface IDriver
{
    /// <summary>First callback; allocate driver-wide resources here (WDF: EvtDriverContextCreate/WdfDriverCreate).</summary>
    Task OnDriverEntryAsync(DriverContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Called once per device instance. Create your <see cref="DeviceBase"/> subclass
    /// wrapping <paramref name="deviceContext"/> and return it; the host then runs the
    /// power-up sequence. Returning null/throwing fails device start.
    /// </summary>
    Task<DeviceBase> OnDeviceAddAsync(DeviceAddContext addContext, DeviceContext deviceContext, CancellationToken cancellationToken);

    /// <summary>Last callback before unload; release driver-wide resources here.</summary>
    Task OnDriverUnloadAsync();
}

/// <summary>Optional base class providing no-op lifecycle callbacks and a CreateDevice helper.</summary>
public abstract class DriverBase : IDriver
{
    protected DriverContext? Context { get; private set; }

    public virtual Task OnDriverEntryAsync(DriverContext context, CancellationToken cancellationToken)
    {
        Context = context;
        return Task.CompletedTask;
    }

    public abstract Task<DeviceBase> OnDeviceAddAsync(DeviceAddContext addContext, DeviceContext deviceContext, CancellationToken cancellationToken);

    public virtual Task OnDriverUnloadAsync() => Task.CompletedTask;

    /// <summary>Helper so subclasses can simply `return CreateDevice&lt;MyDevice&gt;(deviceContext);`.</summary>
    protected TDevice CreateDevice<TDevice>(DeviceContext deviceContext) where TDevice : DeviceBase
    {
        return (TDevice)Activator.CreateInstance(typeof(TDevice), deviceContext)!;
    }
}

/// <summary>
/// Per-driver environment handed to <see cref="IDriver.OnDriverEntryAsync"/>.
/// </summary>
public sealed class DriverContext
{
    public required IDriverLog Log { get; init; }

    /// <summary>Driver service Parameters-key access. Real deployments read the registry; tests use MemoryRegistry.</summary>
    public required IDriverRegistry Registry { get; init; }

    /// <summary>
    /// Clock abstraction for all time-dependent behavior (audio positions, sensor
    /// sampling). Hosts inject TimeProvider.System; tests inject a controllable fake.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Service name from the INF (matches the [Services] section).</summary>
    public string DriverServiceName { get; init; } = string.Empty;

    /// <summary>Driver-global properties.</summary>
    public PropertyStore DriverProperties { get; } = new();
}
