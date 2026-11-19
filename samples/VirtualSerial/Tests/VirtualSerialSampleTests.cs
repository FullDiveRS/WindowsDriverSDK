using System.Text;
using VirtualSerial.Driver;
using WindowsDriver.Sdk;
using WindowsDriver.Sdk.Serial;
using WindowsDriver.Testing;
using WindowsDriver.InfGen;
using Xunit;

namespace VirtualSerial.Tests;

public class VirtualSerialSampleTests
{
    private static async Task<(TestDriverHost Host, TestDevice Device)> StartHostAsync()
    {
        var host = TestDriverHost.Builder().UseFakeTime().Build();
        await host.StartAsync<SampleSerialDriver>("WdsVirtualSerial");
        var device = await host.AddDeviceAsync("ROOT\\CONTOSOVIRTUALSERIAL\\0000", "Root\\ContosoVirtualSerial");
        return (host, device);
    }

    [Fact]
    public async Task Loopback_write_comes_back_on_read()
    {
        var (host, device) = await StartHostAsync();

        var payload = Encoding.ASCII.GetBytes("PING from the control plane\r\n");
        var written = await device.WriteAsync(payload);
        Check.Success(written);
        Check.Bytes(written, payload.Length);

        var read = await device.ReadAsync(payload.Length);
        Check.Success(read);
        Check.Bytes(read, payload.Length);
        Assert.Equal(payload, read.Output.Take(payload.Length).ToArray());

        // Queue is drained now.
        var count = await device.SendIoctlAsync(SerialIoctlCodes.GetRxCount, outputSize: sizeof(uint));
        Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(count.Output));

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Transmit_ioctl_also_loops_back()
    {
        var (host, device) = await StartHostAsync();

        var result = await device.SendIoctlAsync(SerialIoctlCodes.Transmit, Encoding.ASCII.GetBytes("OK"));
        Check.Success(result);
        Check.Bytes(result, 2);

        var read = await device.ReadAsync(2);
        Assert.Equal("OK", Encoding.ASCII.GetString(read.Output, 0, read.BytesTransferred));

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Line_config_roundtrip_and_validation()
    {
        var (host, device) = await StartHostAsync();

        // Default config from the sample driver.
        var config = await device.SendIoctlAsync(SerialIoctlCodes.GetLineConfig, outputSize: SerialLineConfig.WireSize);
        Check.Success(config);
        Check.Bytes(config, SerialLineConfig.WireSize);
        Assert.Equal(115200u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(config.Output));

        // Change to 9600 7E2 with RTS/CTS.
        var newConfig = new SerialLineConfig(9600, 7, SerialParity.Even, SerialStopBits.Two, SerialFlowControl.RtsCts);
        var payload = new byte[SerialLineConfig.WireSize];
        newConfig.WriteTo(payload);
        Check.Success(await device.SendIoctlAsync(SerialIoctlCodes.SetLineConfig, payload, SerialLineConfig.WireSize));

        var after = await device.SendIoctlAsync(SerialIoctlCodes.GetLineConfig, outputSize: SerialLineConfig.WireSize);
        Assert.Equal(newConfig, SerialLineConfig.ReadFrom(after.Output.AsSpan(0, SerialLineConfig.WireSize)));

        // 1.5 stop bits with 8 data bits is invalid.
        new SerialLineConfig(9600, 8, SerialParity.None, SerialStopBits.OnePointFive).WriteTo(payload);
        var rejected = await device.SendIoctlAsync(SerialIoctlCodes.SetLineConfig, payload);
        Check.Status(rejected, NtStatus.InvalidParameter);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Purge_clears_selected_queues()
    {
        var (host, device) = await StartHostAsync();

        await device.WriteAsync(Encoding.ASCII.GetBytes("stale"));
        await device.SendIoctlAsync(SerialSampleIoctlCodes.SimulateRemoteData, Encoding.ASCII.GetBytes("remote"));

        var purge = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(purge, 0x3); // RX + TX
        Check.Success(await device.SendIoctlAsync(SerialIoctlCodes.Purge, purge));

        var count = await device.SendIoctlAsync(SerialIoctlCodes.GetRxCount, outputSize: sizeof(uint));
        Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(count.Output));

        var read = await device.ReadAsync(16);
        Check.Bytes(read, 0);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Status_counters_and_remote_simulation()
    {
        var (host, device) = await StartHostAsync();

        Check.Success(await device.SendIoctlAsync(SerialSampleIoctlCodes.SimulateRemoteData, "hello"u8.ToArray()));
        var read = await device.ReadAsync(5);
        Assert.Equal("hello", Encoding.ASCII.GetString(read.Output, 0, read.BytesTransferred));

        var status = await device.SendIoctlAsync(SerialIoctlCodes.GetStatus, outputSize: 24);
        Check.Success(status);
        Check.Bytes(status, 24);
        Assert.Equal(5ul, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(status.Output.AsSpan(8))); // rx bytes read
        Assert.Equal(1u, status.Output[20]);  // CTS asserted
        Assert.Equal(1u, status.Output[21]);  // DSR asserted
        Assert.Equal(1u, status.Output[23]);  // loopback enabled

        // Break condition bumps the framing-error counter.
        ((SampleSerialDevice)device.Device).AssertBreak(true);
        var status2 = await device.SendIoctlAsync(SerialIoctlCodes.GetStatus, outputSize: 24);
        Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(status2.Output.AsSpan(16)));
        Assert.Equal(1u, status2.Output[22]); // break active

        await host.ShutdownAsync();
    }

    [Fact]
    public void Null_modem_pair_routes_data_across_devices()
    {
        var host = TestDriverHost.Builder().UseFakeTime().Build();

        var portA = new SampleSerialDevice(
            new DeviceContext(CreateDriverContext(host), new DeviceAddContext { DeviceInstanceId = "ROOT\\CONTOSOVIRTUALSERIAL\\000A" }),
            new SerialDeviceOptions { Loopback = false });
        var portB = new SampleSerialDevice(
            new DeviceContext(CreateDriverContext(host), new DeviceAddContext { DeviceInstanceId = "ROOT\\CONTOSOVIRTUALSERIAL\\000B" }),
            new SerialDeviceOptions { Loopback = false });
        _ = new SerialLoopbackPair(portA, portB);

        // A transmits → B receives (and nothing loops back to A).
        Assert.Equal(6, portA.Transmit("from A"u8.ToArray()));
        var buffer = new byte[16];
        Assert.Equal(0, portA.RxQueue.Count);
        Assert.Equal(6, portB.RxQueue.Read(buffer));
        Assert.Equal("from A", Encoding.ASCII.GetString(buffer, 0, 6));

        // And the other direction.
        Assert.Equal(6, portB.Transmit("from B"u8.ToArray()));
        Assert.Equal(6, portA.RxQueue.Read(buffer));
        Assert.Equal("from B", Encoding.ASCII.GetString(buffer, 0, 6));
    }

    [Fact]
    public void Manifest_generates_a_valid_serial_inf()
    {
        var manifest = DriverManifest.FromJson(File.ReadAllText(FindManifest()));
        var inf = new WindowsDriver.InfGen.InfGenerator(new DateOnly(2026, 11, 18)).Generate(manifest);

        Assert.Contains("Class=Ports", inf);
        var issues = new WindowsDriver.InfGen.InfValidator().Validate(inf);
        Assert.True(issues.Count == 0, "INF issues: " + string.Join("; ", issues.Select(i => $"[{i.Section}] {i.Message}")));
    }

    private static DriverContext CreateDriverContext(TestDriverHost host) => new()
    {
        Log = host.Log,
        Registry = host.Registry,
        TimeProvider = host.FakeTime ?? TimeProvider.System,
    };

    private static string FindManifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "../../../../manifest.json");
        return File.Exists(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), "manifest.json");
    }
}
