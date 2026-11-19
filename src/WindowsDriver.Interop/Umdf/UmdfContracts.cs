namespace WindowsDriver.Interop.Umdf;

/// <summary>
/// C# mirrors of the key UMDF COM interfaces. These are *documentation
/// contracts*: they describe which callbacks the native shim implements on the
/// managed driver's behalf, with the same names and semantics as the real
/// interfaces from wudfddi.h.
///
/// The shim (C++) implements the real COM interfaces (IWDFDriver, IWDFDevice,
/// IWDFIoQueue, IWDFIoRequest, ...) with their genuine IIDs from the WDK and is
/// registered in the INF as the UMDF service; it then forwards every callback
/// into the managed host via <see cref="ManagedDriverBootstrap"/>. This layer
/// deliberately avoids fake [ComImport] GUIDs — wrong IIDs would produce
/// binaries that silently fail at runtime. When adding a new forwarded
/// callback, mirror the vtable order from the WDK header.
/// </summary>
public interface IWdfDriver
{
    /// <summary>IWDFDriver::CreateDevice — the shim creates a framework device.</summary>
    void CreateDevice(IWdfDeviceInitialize init, ILifeTime lifetime);
}

public interface ILifeTime
{
    void OnDriverEntry();
    void OnDeviceAdd(string deviceInstanceId, string[] hardwareIds);
    void OnDriverUnload();
}

public interface IWdfDeviceInitialize
{
    void SetPnpPowerCallbacks();
    void SetFileObjectConfig();
}

public interface IWdfDevice
{
    void CreateDeviceInterface(Guid interfaceClassGuid, string? referenceString);
    IWdfIoQueue CreateIoQueue();
    void ConfigureRequestHandling();
}

public interface IWdfIoQueue
{
    void DispatchSequential();
    void DispatchParallel();
    void DispatchManual();
}

public interface IWdfIoRequest
{
    uint IoControlCode { get; }
    nint InputBuffer { get; }
    int InputBufferLength { get; }
    nint OutputBuffer { get; }
    int OutputBufferLength { get; }
    void Complete(int ntStatus);
    void CompleteWithInformation(int ntStatus, int information);
}

public interface IWdfFile
{
    IWdfDevice GetDevice();
}

/// <summary>The subset of UMDF version information the shim reports to the managed host.</summary>
public sealed record UmdfHostInfo(string DriverServiceName, string RegistryPath, int UmdfVersionMajor, int UmdfVersionMinor);
