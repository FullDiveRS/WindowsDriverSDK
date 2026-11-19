using System.Buffers.Binary;
using VirtualSensor.Driver;
using WindowsDriver.Sdk;
using WindowsDriver.Sdk.Sensor;
using WindowsDriver.Testing;
using WindowsDriver.InfGen;
using Xunit;

namespace VirtualSensor.Tests;

public class VirtualSensorSampleTests
{
    private static async Task<(TestDriverHost Host, TestDevice Device)> StartHostAsync()
    {
        var host = TestDriverHost.Builder().UseFakeTime().Build();
        await host.StartAsync<SampleSensorDriver>("WdsVirtualSensor");
        var device = await host.AddDeviceAsync("ROOT\\CONTOSOVIRTUALSENSOR\\0000", "Root\\ContosoVirtualSensor");
        return (host, device);
    }

    [Fact]
    public async Task Device_reports_two_sensors()
    {
        var (host, device) = await StartHostAsync();

        var count = await device.SendIoctlAsync(SensorIoctlCodes.GetSensorCount, outputSize: sizeof(uint));
        Check.Success(count);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(count.Output));

        var info = await device.SendIoctlAsync(SensorIoctlCodes.GetSensorInfo, PackUInt(0), outputSize: 28);
        Check.Success(info);
        Assert.Equal((uint)SensorKind.Accelerometer3D, BinaryPrimitives.ReadUInt32LittleEndian(info.Output));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(info.Output.AsSpan(4)));
        Assert.Equal(50000u, BinaryPrimitives.ReadUInt32LittleEndian(info.Output.AsSpan(8))); // 50 Hz in milliHz

        var als = await device.SendIoctlAsync(SensorIoctlCodes.GetSensorInfo, PackUInt(1), outputSize: 28);
        Assert.Equal((uint)SensorKind.AmbientLight, BinaryPrimitives.ReadUInt32LittleEndian(als.Output));

        var outOfRange = await device.SendIoctlAsync(SensorIoctlCodes.GetSensorInfo, PackUInt(5), outputSize: 28);
        Check.Status(outOfRange, NtStatus.InvalidParameter);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Readings_are_deterministic_and_gravity_is_present()
    {
        var (host, device) = await StartHostAsync();
        var sensorDevice = (SampleSensorDevice)device.Device;

        // Before sampling, latest readings are absent.
        var none = await device.SendIoctlAsync(SensorIoctlCodes.GetLatestReading, PackUInt(0));
        Check.Status(none, NtStatus.DeviceNotReady);

        sensorDevice.SampleOnce();
        var reading = sensorDevice.LatestReading(0)!;
        Assert.Equal(3, reading.Values.Count);
        Assert.Equal("m/s^2", reading.Unit);
        // Device lies flat: Z ≈ +9.8, X/Y near zero.
        Assert.InRange(reading.Values[2], 9.0, 10.6);
        Assert.InRange(reading.Values[0], -0.5, 0.5);
        Assert.InRange(reading.Values[1], -0.5, 0.5);

        // Same timestamp → identical values (pure function of time).
        var again = ((AccelerometerSimulator)sensorDevice.GetSource(0)).Sample(reading.Timestamp);
        Assert.Equal(reading.Values, again.Values);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task GetLatestReading_ioctl_returns_the_sampled_values()
    {
        var (host, device) = await StartHostAsync();
        var sensorDevice = (SampleSensorDevice)device.Device;

        host.FakeTime!.Advance(TimeSpan.FromMinutes(3));
        sensorDevice.SampleOnce();

        var result = await device.SendIoctlAsync(SensorIoctlCodes.GetLatestReading, PackUInt(1), outputSize: 8 + 4 + 8);
        Check.Success(result);
        Check.Bytes(result, 20);
        var valueCount = BinaryPrimitives.ReadUInt32LittleEndian(result.Output.AsSpan(8));
        Assert.Equal(1u, valueCount);
        var lux = BinaryPrimitives.ReadDoubleLittleEndian(result.Output.AsSpan(12));
        // 3 minutes past fake midnight → no daylight, indoor baseline.
        Assert.InRange(lux, 300, 500);

        // Timestamp is the fake clock's.
        var ticks = BinaryPrimitives.ReadUInt64LittleEndian(result.Output);
        var timestamp = new DateTimeOffset((long)ticks, TimeSpan.Zero);
        Assert.Equal(host.FakeTime.GetUtcNow().UtcTicks, timestamp.UtcTicks);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Sample_specific_ioctls_control_the_simulation()
    {
        var (host, device) = await StartHostAsync();
        var sensorDevice = (SampleSensorDevice)device.Device;

        // ALS override: force 12345 lux.
        var overridePayload = new byte[1 + sizeof(double)];
        overridePayload[0] = 1;
        BinaryPrimitives.WriteDoubleLittleEndian(overridePayload.AsSpan(1), 12345);
        Check.Success(await device.SendIoctlAsync(SensorSampleIoctlCodes.SetAmbientOverride, overridePayload));

        sensorDevice.SampleOnce();
        Assert.Equal(12345, sensorDevice.LatestReading(1)!.Values[0], precision: 3);

        // Disable override → back to simulation.
        overridePayload[0] = 0;
        await device.SendIoctlAsync(SensorSampleIoctlCodes.SetAmbientOverride, overridePayload);
        sensorDevice.SampleOnce();
        Assert.True(sensorDevice.LatestReading(1)!.Values[0] < 1000, "override did not clear");

        // Invalid override value.
        overridePayload[0] = 1;
        BinaryPrimitives.WriteDoubleLittleEndian(overridePayload.AsSpan(1), -5);
        var bad = await device.SendIoctlAsync(SensorSampleIoctlCodes.SetAmbientOverride, overridePayload);
        Check.Status(bad, NtStatus.InvalidParameter);

        // Impulse on the accelerometer.
        var impulse = new byte[3 * sizeof(double) + sizeof(uint)];
        BinaryPrimitives.WriteDoubleLittleEndian(impulse.AsSpan(0), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(impulse.AsSpan(24), 400);
        Check.Success(await device.SendIoctlAsync(SensorSampleIoctlCodes.InjectImpulse, impulse));

        // The impulse ramps up linearly — sample mid-way through the 400 ms window.
        host.FakeTime!.Advance(TimeSpan.FromMilliseconds(200));
        sensorDevice.SampleOnce();
        var shaken = sensorDevice.LatestReading(0)!.Values;
        Assert.True(shaken[0] > 5, $"impulse not visible: X={shaken[0]}");

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Event_queue_receives_sampled_readings()
    {
        var (host, device) = await StartHostAsync();
        var sensorDevice = (SampleSensorDevice)device.Device;

        sensorDevice.SampleOnce();
        sensorDevice.SampleOnce();

        var first = await sensorDevice.Readings.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
        var second = await sensorDevice.Readings.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
        Assert.Equal(SensorKind.Accelerometer3D, first.Kind);
        Assert.Equal(SensorKind.AmbientLight, second.Kind);

        await host.ShutdownAsync();
    }

    [Fact]
    public void Manifest_generates_a_valid_sensor_inf()
    {
        var manifest = DriverManifest.FromJson(File.ReadAllText(FindManifest()));
        var inf = new WindowsDriver.InfGen.InfGenerator(new DateOnly(2026, 11, 18)).Generate(manifest);

        Assert.Contains("Class=Sensor", inf);
        var issues = new WindowsDriver.InfGen.InfValidator().Validate(inf);
        Assert.True(issues.Count == 0, "INF issues: " + string.Join("; ", issues.Select(i => $"[{i.Section}] {i.Message}")));
    }

    private static byte[] PackUInt(uint value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        return data;
    }

    private static string FindManifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "../../../../manifest.json");
        return File.Exists(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), "manifest.json");
    }
}
