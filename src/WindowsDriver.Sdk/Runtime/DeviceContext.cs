namespace WindowsDriver.Sdk;

/// <summary>
/// Per-device environment handed to the driver when a device instance is added.
/// Created by the host; the driver passes it into its <see cref="DeviceBase"/>
/// subclass constructor.
/// </summary>
public sealed class DeviceContext
{
    private readonly List<string> _deviceInterfaces = [];

    /// <summary>
    /// Created by a host (DriverHost, the UMDF shim, the test host, or a test)
    /// and handed to <see cref="IDriver.OnDeviceAddAsync"/>. Tests and samples
    /// may construct one directly for unit-level device tests.
    /// </summary>
    public DeviceContext(DriverContext driver, DeviceAddContext add)
    {
        Driver = driver;
        AddContext = add;
        Log = new ScopedDriverLog(driver.Log, add.DeviceInstanceId);
    }

    public DriverContext Driver { get; }

    internal DeviceAddContext AddContext { get; }

    /// <summary>Bus-reported device instance id.</summary>
    public string DeviceInstanceId => AddContext.DeviceInstanceId;

    public IReadOnlyList<string> HardwareIds => AddContext.HardwareIds;
    public IReadOnlyList<string> CompatibleIds => AddContext.CompatibleIds;

    /// <summary>Devnode property store (per-device).</summary>
    public PropertyStore Properties { get; } = new();

    /// <summary>Device-scoped log with the instance id prefixed.</summary>
    public IDriverLog Log { get; }

    /// <summary>Device interfaces created so far (symbolic link names).</summary>
    public IReadOnlyList<string> DeviceInterfaces => _deviceInterfaces;

    /// <summary>
    /// Registers a device interface (WdfDeviceCreateDeviceInterface). Returns the
    /// symbolic link name that applications open. The generated name is host-specific:
    /// the test host fabricates a stable pseudo-path; a UMDF shim returns the real
    /// "\\?\..." path.
    /// </summary>
    public string CreateDeviceInterface(Guid interfaceClass, string? referenceString = null)
    {
        var symlink = $"\\\\?\\{interfaceClass:B}#{referenceString ?? string.Empty}".ToUpperInvariant();
        _deviceInterfaces.Add(symlink);
        Properties.Set(KnownDeviceProperties.InterfaceClassGuid, interfaceClass);
        Log.Info("created device interface {0}", symlink);
        return symlink;
    }
}
