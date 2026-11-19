using System.Buffers.Binary;
using HidGamepad.Driver;
using WindowsDriver.Sdk;
using WindowsDriver.Sdk.Hid;
using WindowsDriver.Testing;
using WindowsDriver.InfGen;
using Xunit;

namespace HidGamepad.Tests;

public class HidGamepadSampleTests
{
    private static async Task<(TestDriverHost Host, TestDevice Device)> StartHostAsync()
    {
        var host = TestDriverHost.Builder().UseFakeTime().Build();
        await host.StartAsync<SampleGamepadDriver>("WdsHidGamepad");
        var device = await host.AddDeviceAsync("ROOT\\CONTOSOVIRTUALGAMEPAD\\0000", "Root\\ContosoVirtualGamepad");
        return (host, device);
    }

    [Fact]
    public async Task Report_descriptor_is_valid_and_matches_the_report_layout()
    {
        var (host, device) = await StartHostAsync();

        var descriptor = await device.SendIoctlAsync(HidIoctlCodes.GetReportDescriptor, outputSize: 512);
        Check.Success(descriptor);
        Assert.True(descriptor.BytesTransferred > 20, "descriptor looks too small");

        var summary = HidReportDescriptorParser.Parse(descriptor.Output.AsSpan(0, descriptor.BytesTransferred));
        Assert.True(summary.UsesReportIds);
        var report = summary.Find(GamepadInputReport.ReportId);
        Assert.NotNull(report);
        Assert.Equal(12 + 4 + 6 * 16, report!.InputBits); // buttons + pad + 6 axes
        Assert.Equal(GamepadInputReport.ReportSizeBytes, report.WireBytes(hasReportId: true));

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Class_descriptor_announces_report_descriptor_length()
    {
        var (host, device) = await StartHostAsync();

        var classDescriptor = await device.SendIoctlAsync(HidIoctlCodes.GetDeviceDescriptor, outputSize: 64);
        Check.Success(classDescriptor);
        Check.Bytes(classDescriptor, HidDescriptor.HidDescriptorLength);
        Assert.Equal(HidDescriptor.HidDescriptorType, classDescriptor.Output[1]);
        Assert.Equal(0x11, classDescriptor.Output[2]); // bcdHID 1.11 = 0x0111 (LSB)
        Assert.Equal(0x01, classDescriptor.Output[3]); // ...MSB

        var size = await device.SendIoctlAsync(HidIoctlCodes.GetInputReportSize, outputSize: 2);
        Check.Success(size);
        Assert.Equal(GamepadInputReport.ReportSizeBytes, BinaryPrimitives.ReadUInt16LittleEndian(size.Output));

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Read_returns_the_current_input_report()
    {
        var (host, device) = await StartHostAsync();

        var state = new GamepadState();
        state.SetButtons(1, 4, 12);
        state.X = -1234;
        state.Rz = 32100;

        var update = BuildUpdatePayload(state);
        Check.Success(await device.SendIoctlAsync(GamepadIoctlCodes.UpdateState, update));

        var report = await device.ReadAsync(GamepadInputReport.ReportSizeBytes);
        Check.Success(report);
        Check.Bytes(report, GamepadInputReport.ReportSizeBytes);
        Assert.Equal(GamepadInputReport.ReportId, report.Output[0]);

        var unpacked = GamepadInputReport.Unpack(report.Output.AsSpan(0, report.BytesTransferred));
        Assert.True(unpacked[1]);
        Assert.True(unpacked[4]);
        Assert.True(unpacked[12]);
        Assert.False(unpacked[2]);
        Assert.Equal(-1234, unpacked.X);
        Assert.Equal(32100, unpacked.Rz);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task SetInputState_ioctl_replaces_the_whole_state()
    {
        var (host, device) = await StartHostAsync();

        var raw = new byte[GamepadInputReport.ReportSizeBytes];
        raw[0] = GamepadInputReport.ReportId;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(1), 0b0000_1100_0000_0011); // buttons 1,2,11,12
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(3), 500);

        Check.Success(await device.SendIoctlAsync(GamepadIoctlCodes.SetInputState, raw));

        var snapshot = ((SampleGamepadDevice)device.Device).State;
        Assert.True(snapshot[1] && snapshot[2] && snapshot[11] && snapshot[12]);
        Assert.False(snapshot[5]);
        Assert.Equal(500, snapshot.X);

        // Wrong-size input is rejected.
        var bad = await device.SendIoctlAsync(GamepadIoctlCodes.SetInputState, new byte[7]);
        Check.Status(bad, NtStatus.InvalidParameter);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Feature_reports_echo_back()
    {
        var (host, device) = await StartHostAsync();

        var feature = new byte[] { 0x05, 0x7F, 0x00, 0x99 };
        Check.Success(await device.SendIoctlAsync(HidIoctlCodes.SetFeatureReport, feature));

        var echoed = await device.SendIoctlAsync(HidIoctlCodes.GetFeatureReport, outputSize: feature.Length);
        Check.Success(echoed);
        Check.Bytes(echoed, feature.Length);
        Assert.Equal(feature, echoed.Output.Take(feature.Length).ToArray());

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Short_output_buffers_get_buffer_too_small()
    {
        var (host, device) = await StartHostAsync();
        var report = await device.ReadAsync(4); // needs 15 bytes
        Check.Status(report, NtStatus.BufferTooSmall);
        await host.ShutdownAsync();
    }

    [Fact]
    public void Manifest_generates_a_valid_hid_inf()
    {
        var manifest = DriverManifest.FromJson(File.ReadAllText(FindManifest()));
        var inf = new WindowsDriver.InfGen.InfGenerator(new DateOnly(2026, 11, 18)).Generate(manifest);

        Assert.Contains("Class=HIDClass", inf);
        Assert.Contains("{745a17a0-74d3-11d0-b6fe-00a0c90e91ab}", inf);

        var issues = new WindowsDriver.InfGen.InfValidator().Validate(inf);
        Assert.True(issues.Count == 0, "INF issues: " + string.Join("; ", issues.Select(i => $"[{i.Section}] {i.Message}")));
    }

    private static byte[] BuildUpdatePayload(GamepadState state)
    {
        var payload = new byte[2 + 6 * sizeof(short)];
        ushort buttons = 0;
        for (var b = 1; b <= GamepadState.ButtonCount; b++)
        {
            if (state[b])
            {
                buttons |= (ushort)(1 << (b - 1));
            }
        }
        BinaryPrimitives.WriteUInt16LittleEndian(payload, buttons);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), state.X);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(4), state.Y);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(6), state.Z);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(8), state.Rx);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(10), state.Ry);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(12), state.Rz);
        return payload;
    }

    private static string FindManifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "../../../../manifest.json");
        return File.Exists(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), "manifest.json");
    }
}
