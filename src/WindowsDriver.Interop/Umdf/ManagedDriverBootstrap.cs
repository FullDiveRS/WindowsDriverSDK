using System.Runtime.InteropServices;
using WindowsDriver.Sdk;

namespace WindowsDriver.Interop.Umdf;

/// <summary>
/// Settings passed from the native UMDF shim into the managed bootstrap. The
/// layout is mirrored byte-for-byte by the shim (see shim/ and docs/umdf-guide.md).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UmdfShimSettings
{
    /// <summary>Function pointer the shim provides for completing requests: int CompleteRequest(ulong id, int status, int bytes).</summary>
    public nint CompleteRequest;

    /// <summary>Function pointer for host-side logging: void Log(int level, void* utf8Message, int length).</summary>
    public nint Log;

    /// <summary>Function pointer the managed side calls when a queue must be armed again (manual dispatch).</summary>
    public nint ArmQueue;

    /// <summary>UTF-8 service name pointer (null-terminated), owned by the shim.</summary>
    public nint DriverServiceNameUtf8;

    /// <summary>UTF-8 fully-qualified managed driver type name, e.g. "VirtualAudio.Driver.SampleAudioDriver, VirtualAudio.Driver".</summary>
    public nint DriverTypeNameUtf8;

    /// <summary>5 pointers — 8 bytes each on 64-bit Windows.</summary>
    public static readonly int WireSize = 5 * IntPtr.Size;
}

/// <summary>
/// Entry points the native UMDF shim calls after hosting the .NET runtime with
/// hostfxr (load_assembly_and_get_function_pointer). All callbacks are
/// [UnmanagedCallersOnly] so the shim receives plain function pointers.
///
/// Marshaling rules: requests are pushed with WdsQueueRequest;
/// the managed side completes them via the shim's CompleteRequest callback.
/// NTSTATUS conventions match <see cref="NtStatus"/>.
/// </summary>
public static unsafe class ManagedDriverBootstrap
{
    private static DriverHost? _host;
    private static nint _completeRequestPtr;
    private static nint _logPtr;

    /// <summary>hostfxr delegates here; returns an NTSTATUS (0 = success).</summary>
    [UnmanagedCallersOnly(EntryPoint = "WdsInitialize")]
    public static int Initialize(UmdfShimSettings* settings)
    {
        try
        {
            if (settings is null)
            {
                return AsInt(NtStatus.InvalidParameter);
            }
            _completeRequestPtr = settings->CompleteRequest;
            _logPtr = settings->Log;

            var serviceName = ReadUtf8(settings->DriverServiceNameUtf8) ?? "WdsDriver";
            var driverTypeName = ReadUtf8(settings->DriverTypeNameUtf8)
                                 ?? throw new DriverException(NtStatus.InvalidParameter, "DriverTypeNameUtf8 missing from shim settings.");

            var log = new NativeShimLog(_logPtr);
            _host = new DriverHost(log, registry: null, timeProvider: TimeProvider.System);

            var driverType = Type.GetType(driverTypeName, throwOnError: true)!;
            _host.StartAsync(driverType, serviceName).GetAwaiter().GetResult();
            return AsInt(NtStatus.Success);
        }
        catch (DriverException ex)
        {
            return AsInt(ex.Status);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"WdsInitialize failed: {ex}");
            return AsInt(NtStatus.Unsuccessful);
        }
    }

    /// <summary>PnP: a device instance was reported. hardwareIds is an array of pointers to null-terminated UTF-16 strings.</summary>
    [UnmanagedCallersOnly(EntryPoint = "WdsDeviceAdd")]
    public static int DeviceAdd(byte* deviceInstanceIdUtf16, byte** hardwareIdsUtf16, int hardwareIdCount)
    {
        try
        {
            var host = _host ?? throw new DriverException(NtStatus.InvalidDeviceState, "Shim not initialized.");
            var instanceId = Marshal.PtrToStringUni((nint)deviceInstanceIdUtf16) ?? string.Empty;
            var hardwareIds = new List<string>(hardwareIdCount);
            for (var i = 0; i < hardwareIdCount; i++)
            {
                hardwareIds.Add(Marshal.PtrToStringUni((nint)hardwareIdsUtf16[i]) ?? string.Empty);
            }
            var addContext = new DeviceAddContext
            {
                DeviceInstanceId = instanceId,
                HardwareIds = hardwareIds,
            };
            _ = host.AddDeviceAsync(addContext).GetAwaiter().GetResult();
            return AsInt(NtStatus.Success);
        }
        catch (DriverException ex)
        {
            return AsInt(ex.Status);
        }
        catch (Exception ex)
        {
            ReportCrash("WdsDeviceAdd", ex);
            return AsInt(NtStatus.Unsuccessful);
        }
    }

    /// <summary>Pushes an IO request from the UMDF shim into the managed driver. kind: 0=DeviceControl, 1=Read, 2=Write.</summary>
    [UnmanagedCallersOnly(EntryPoint = "WdsQueueRequest")]
    public static int QueueRequest(
        ulong requestId,
        int kind,
        uint ioControlCode,
        byte* input,
        int inputLength,
        byte* output,
        int outputLength)
    {
        try
        {
            var host = _host ?? throw new DriverException(NtStatus.InvalidDeviceState, "Shim not initialized.");
            var device = host.Devices.FirstOrDefault() ?? throw new DriverException(NtStatus.NoSuchDevice, "No device instances.");
            var managedKind = kind switch
            {
                0 => IoRequestKind.DeviceControl,
                1 => IoRequestKind.Read,
                2 => IoRequestKind.Write,
                _ => throw new DriverException(NtStatus.InvalidParameter, $"Unknown request kind {kind}."),
            };
            var inputCopy = inputLength > 0 ? new ReadOnlySpan<byte>(input, inputLength).ToArray() : Array.Empty<byte>();
            var outputCopy = outputLength > 0 ? new byte[outputLength] : Array.Empty<byte>();

            var request = new IoRequest(managedKind, ioControlCode, inputCopy, outputCopy, requestId);
            request.Completed += completed =>
            {
                if (_completeRequestPtr != nint.Zero)
                {
                    var complete = (delegate* unmanaged<ulong, int, int, int>)_completeRequestPtr;
                    _ = complete(completed.Id, (int)completed.Status, completed.BytesTransferred);
                }
                if (outputLength > 0 && completed.BytesTransferred > 0 && output != null)
                {
                    completed.OutputBuffer.Span.Slice(0, completed.BytesTransferred)
                        .CopyTo(new Span<byte>(output, outputLength));
                }
            };
            device.DefaultQueue.Submit(request);
            return AsInt(NtStatus.Success);
        }
        catch (DriverException ex)
        {
            return AsInt(ex.Status);
        }
        catch (Exception ex)
        {
            ReportCrash("WdsQueueRequest", ex);
            return AsInt(NtStatus.Unsuccessful);
        }
    }

    /// <summary>Power: device entered D0 (previousState: 0 unspecified, 1 D0, 2 D1, 3 D2, 4 D3).</summary>
    [UnmanagedCallersOnly(EntryPoint = "WdsDeviceD0Entry")]
    public static int DeviceD0Entry(int previousState) => RunLifecycle((device, ct) => device.D0EntryAsync((DevicePowerState)previousState, ct));

    /// <summary>Power: device is leaving D0 (newState as above).</summary>
    [UnmanagedCallersOnly(EntryPoint = "WdsDeviceD0Exit")]
    public static int DeviceD0Exit(int newState) => RunLifecycle((device, ct) => device.D0ExitAsync((DevicePowerState)newState, ct));

    /// <summary>PnP release: free hardware resources.</summary>
    [UnmanagedCallersOnly(EntryPoint = "WdsDeviceRelease")]
    public static int DeviceRelease() => RunLifecycle((device, ct) => device.ReleaseHardwareAsync(ct));

    /// <summary>Final unload.</summary>
    [UnmanagedCallersOnly(EntryPoint = "WdsDriverUnload")]
    public static int DriverUnload()
    {
        try
        {
            var host = _host;
            _host = null;
            if (host is not null)
            {
                host.ShutdownAsync().GetAwaiter().GetResult();
            }
            return AsInt(NtStatus.Success);
        }
        catch (Exception ex)
        {
            ReportCrash("WdsDriverUnload", ex);
            return AsInt(NtStatus.Unsuccessful);
        }
    }

    private static int RunLifecycle(Func<DeviceBase, CancellationToken, Task> action)
    {
        try
        {
            var host = _host ?? throw new DriverException(NtStatus.InvalidDeviceState, "Shim not initialized.");
            foreach (var device in host.Devices)
            {
                action(device.Device, CancellationToken.None).GetAwaiter().GetResult();
            }
            return AsInt(NtStatus.Success);
        }
        catch (DriverException ex)
        {
            return AsInt(ex.Status);
        }
        catch (Exception ex)
        {
            ReportCrash("lifecycle", ex);
            return AsInt(NtStatus.Unsuccessful);
        }
    }

    /// <summary>NTSTATUS values above int.MaxValue (0xC0000000+) need an unchecked widening.</summary>
    private static int AsInt(NtStatus status) => unchecked((int)(uint)status);

    private static string? ReadUtf8(nint pointer)
    {
        if (pointer == nint.Zero)
        {
            return null;
        }
        var length = 0;
        while (((byte*)pointer)[length] != 0)
        {
            length++;
        }
        return System.Text.Encoding.UTF8.GetString((byte*)pointer, length);
    }

    private static void ReportCrash(string where, Exception ex) =>
        System.Console.Error.WriteLine($"[{where}] {ex.GetType().Name}: {ex.Message}");

    private sealed class NativeShimLog(nint sink) : IDriverLog
    {
        public bool IsEnabled(DriverLogLevel level) => sink != nint.Zero;

        public unsafe void Log(DriverLogLevel level, string message)
        {
            if (sink == nint.Zero)
            {
                return;
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(message);
            fixed (byte* p = bytes)
            {
                ((delegate* unmanaged<int, byte*, int, void>)sink)((int)level, p, bytes.Length);
            }
        }
    }
}
