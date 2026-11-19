using WindowsDriver.Sdk;

namespace DriverTemplate;

/// <summary>
/// Driver entry point. The host (test host during development, the UMDF shim in
/// production) calls OnDriverEntryAsync once, then OnDeviceAddAsync per device.
/// </summary>
public sealed class DriverTemplateDriver : DriverBase
{
    public override Task OnDriverEntryAsync(DriverContext context, CancellationToken cancellationToken)
    {
        context.Log.Info("DriverTemplate driver entering (service '{0}')", context.DriverServiceName);
        return Task.CompletedTask;
    }

    public override Task<DeviceBase> OnDeviceAddAsync(DeviceAddContext addContext, DeviceContext deviceContext, CancellationToken cancellationToken)
    {
        deviceContext.Log.Info("device add: {0}", addContext.DeviceInstanceId);
        deviceContext.Properties.Set(KnownDeviceProperties.FriendlyName, "DriverTemplate Device");

        var device = new DriverTemplateDevice(deviceContext);
        // Applications discover the device through its interface GUID.
        deviceContext.CreateDeviceInterface(new Guid("00000000-0000-0000-0000-000000000000"));
        return Task.FromResult<DeviceBase>(device);
    }
}

/// <summary>
/// The device instance. Register IOCTL handlers on the default queue and
/// override lifecycle callbacks (PrepareHardware/D0Entry/…) as needed.
/// </summary>
public sealed class DriverTemplateDevice : DeviceBase
{
    public DriverTemplateDevice(DeviceContext context) : base(context)
    {
        DefaultQueue.OnDeviceControl(HandleDeviceControlAsync);
    }

    public override Task OnPrepareHardwareAsync(CancellationToken cancellationToken)
    {
        Context.Log.Info("hardware prepared");
        return Task.CompletedTask;
    }

    private Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        switch (request.IoControlCode)
        {
            // Vendor IOCTL codes start at 0x800:
            //   var code = WinIoctl.VendorControlCode(0x800, WinIoctl.FileAccessRead);
            default:
                request.Fail(NtStatus.InvalidDeviceRequest);
                break;
        }
        return Task.CompletedTask;
    }
}
