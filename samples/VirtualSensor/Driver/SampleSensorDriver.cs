using System.Buffers.Binary;
using WindowsDriver.Sdk;
using WindowsDriver.Sdk.Sensor;

namespace VirtualSensor.Driver;

/// <summary>Sample-specific IOCTLs.</summary>
public static class SensorSampleIoctlCodes
{
    /// <summary>In: (u8 enable, f64 lux) — overrides the ambient-light simulation.</summary>
    public static readonly uint SetAmbientOverride = WinIoctl.VendorControlCode(0x900, WinIoctl.FileAccessWrite);

    /// <summary>In: (f64 dx, f64 dy, f64 dz, u32 durationMs) — injects a shake impulse.</summary>
    public static readonly uint InjectImpulse = WinIoctl.VendorControlCode(0x901, WinIoctl.FileAccessWrite);
}

/// <summary>
/// Sample driver: one device hosting a 3-axis accelerometer and an ambient
/// light sensor, both deterministic simulators from the SDK.
/// </summary>
public sealed class SampleSensorDriver : DriverBase
{
    public override Task<DeviceBase> OnDeviceAddAsync(DeviceAddContext addContext, DeviceContext deviceContext, CancellationToken cancellationToken)
    {
        deviceContext.Properties.Set(KnownDeviceProperties.FriendlyName, "Contoso Virtual Sensor Suite (SDK sample)");
        deviceContext.Properties.Set(KnownDeviceProperties.Manufacturer, "Contoso Devices");

        var accelerometer = new AccelerometerSimulator(seed: 42, sampleRateHz: 50);
        var ambientLight = new AmbientLightSimulator(seed: 7, sampleRateHz: 4);

        var device = new SampleSensorDevice(deviceContext, new SensorDeviceOptions
        {
            Sources = [accelerometer, ambientLight],
        });
        deviceContext.CreateDeviceInterface(new Guid("7B1E3D6C-9E30-4A5E-9A6B-2D8C7F5A0B41"));
        return Task.FromResult<DeviceBase>(device);
    }
}

/// <summary>Sensor device extended with sample-specific simulation IOCTLs.</summary>
public sealed class SampleSensorDevice : SensorDevice
{
    public SampleSensorDevice(DeviceContext context, SensorDeviceOptions options)
        : base(context, options)
    {
    }

    protected override Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        var code = request.IoControlCode;
        if (code == SensorSampleIoctlCodes.SetAmbientOverride)
        {
            var size = sizeof(byte) + sizeof(double);
            if (request.InputBuffer.Length < size)
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                var enable = request.InputBuffer.Span[0] != 0;
                var lux = BinaryPrimitives.ReadDoubleLittleEndian(request.InputBuffer.Span.Slice(1));
                if (enable && (lux < 0 || lux > 200_000))
                {
                    request.Fail(NtStatus.InvalidParameter);
                }
                else
                {
                    if (GetSource(1) is AmbientLightSimulator als)
                    {
                        als.OverrideLux = enable ? lux : null;
                        Context.Log.Info("ambient override {0} ({1} lux)", enable ? "on" : "off", enable ? lux : 0);
                    }
                    request.Complete(NtStatus.Success, size);
                }
            }
        }
        else if (code == SensorSampleIoctlCodes.InjectImpulse)
        {
            var size = 3 * sizeof(double) + sizeof(uint);
            if (request.InputBuffer.Length < size)
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else if (GetSource(0) is not AccelerometerSimulator accelerometer)
            {
                request.Fail(NtStatus.DeviceNotReady);
            }
            else
            {
                var dx = BinaryPrimitives.ReadDoubleLittleEndian(request.InputBuffer.Span);
                var dy = BinaryPrimitives.ReadDoubleLittleEndian(request.InputBuffer.Span.Slice(8));
                var dz = BinaryPrimitives.ReadDoubleLittleEndian(request.InputBuffer.Span.Slice(16));
                var durationMs = BinaryPrimitives.ReadUInt32LittleEndian(request.InputBuffer.Span.Slice(24));
                accelerometer.AddImpulse(Driver.TimeProvider.GetUtcNow().TimeOfDay, TimeSpan.FromMilliseconds(durationMs), (dx, dy, dz));
                Context.Log.Info("impulse injected: ({0}, {1}, {2}) m/s^2 for {3} ms", dx, dy, dz, durationMs);
                request.Complete(NtStatus.Success, size);
            }
        }
        else
        {
            return base.HandleDeviceControlAsync(request, ct);
        }
        return Task.CompletedTask;
    }
}
