using System.Buffers.Binary;
using WindowsDriver.Sdk;
using WindowsDriver.Sdk.Serial;

namespace VirtualSerial.Driver;

/// <summary>Sample-specific IOCTLs.</summary>
public static class SerialSampleIoctlCodes
{
    /// <summary>In: bytes to inject into the RX queue (simulates a remote peer sending data).</summary>
    public static readonly uint SimulateRemoteData = WinIoctl.VendorControlCode(0x900, WinIoctl.FileAccessWrite);
}

/// <summary>
/// Sample driver: enumerates one virtual COM port per devnode. The default
/// instance is a loopback port; two instances can be cross-connected with
/// <see cref="SerialLoopbackPair"/> to model a cable between them.
/// </summary>
public sealed class SampleSerialDriver : DriverBase
{
    public override Task<DeviceBase> OnDeviceAddAsync(DeviceAddContext addContext, DeviceContext deviceContext, CancellationToken cancellationToken)
    {
        deviceContext.Properties.Set(KnownDeviceProperties.FriendlyName, "Contoso Virtual COM Port (SDK sample)");
        deviceContext.Properties.Set(KnownDeviceProperties.Manufacturer, "Contoso Devices");

        var device = new SampleSerialDevice(deviceContext, new SerialDeviceOptions
        {
            InitialConfig = new SerialLineConfig(BaudRate: 115_200, DataBits: 8, Parity: SerialParity.None, StopBits: SerialStopBits.One),
            Loopback = true,
        });
        deviceContext.CreateDeviceInterface(new Guid("86E0D1E0-8089-4A62-9A0E-3B7C4D9E2F18"));
        return Task.FromResult<DeviceBase>(device);
    }
}

/// <summary>Virtual COM port extended with a remote-peer simulation IOCTL.</summary>
public sealed class SampleSerialDevice : SerialDevice
{
    public SampleSerialDevice(DeviceContext context, SerialDeviceOptions options)
        : base(context, options)
    {
    }

    protected override Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        var code = request.IoControlCode;
        if (code == SerialSampleIoctlCodes.SimulateRemoteData)
        {
            if (request.InputBuffer.Length == 0)
            {
                request.Fail(NtStatus.InvalidParameter);
            }
            else
            {
                var injected = InjectReceive(request.InputBuffer.Span);
                request.Complete(NtStatus.Success, injected);
            }
        }
        else
        {
            return base.HandleDeviceControlAsync(request, ct);
        }
        return Task.CompletedTask;
    }
}
