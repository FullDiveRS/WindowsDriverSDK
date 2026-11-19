using System.Buffers.Binary;
using VirtualAudio.Driver;
using WindowsDriver.Sdk;
using WindowsDriver.Sdk.Audio;
using WindowsDriver.Testing;
using WindowsDriver.InfGen;
using Xunit;

namespace VirtualAudio.Tests;

public class VirtualAudioSampleTests
{
    private static async Task<(TestDriverHost Host, TestDevice Device)> StartHostAsync()
    {
        var host = TestDriverHost.Builder().UseFakeTime().Build();
        await host.StartAsync<SampleAudioDriver>("WdsVirtualAudio");
        var device = await host.AddDeviceAsync(
            "ROOT\\CONTOSOVIRTUALAUDIO\\0000",
            "Root\\ContosoVirtualAudio");
        return (host, device);
    }

    private static byte[] PackFormat(PcmFormat format)
    {
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)format.SampleRateHz);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)format.BitsPerSample);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)format.Channels);
        return data;
    }

    private static PcmFormat ReadFormat(byte[] data) => new(
        (int)BinaryPrimitives.ReadUInt32LittleEndian(data),
        (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)),
        (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)));

    [Fact]
    public async Task Device_boots_and_reports_default_format()
    {
        var (host, device) = await StartHostAsync();

        var result = await device.SendIoctlAsync(AudioIoctlCodes.GetFormat, outputSize: 12);
        Check.Success(result);
        Check.Bytes(result, 12);
        Assert.Equal(new PcmFormat(44100, 16, 2), ReadFormat(result.Output));

        Assert.Single(device.Context.DeviceInterfaces);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Format_negotiation_adjusts_unsupported_requests()
    {
        var (host, device) = await StartHostAsync();

        // 48000 is supported exactly.
        var exact = await device.SendIoctlAsync(AudioIoctlCodes.SetFormat, PackFormat(new PcmFormat(48000, 16, 2)), 12);
        Check.Success(exact);

        // 22050 is not supported: the negotiator picks 44100 and reports the adjusted format.
        var adjusted = await device.SendIoctlAsync(AudioIoctlCodes.SetFormat, PackFormat(new PcmFormat(22050, 16, 2)), 12);
        Check.Success(adjusted);
        Assert.Equal(44100, ReadFormat(adjusted.Output).SampleRateHz);

        // Zero rate is invalid.
        var invalid = await device.SendIoctlAsync(AudioIoctlCodes.SetFormat, PackFormat(new PcmFormat(0, 16, 2)));
        Check.Status(invalid, NtStatus.InvalidParameter);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Format_cannot_change_while_running()
    {
        var (host, device) = await StartHostAsync();
        await device.SendIoctlAsync(AudioIoctlCodes.Start);

        var result = await device.SendIoctlAsync(AudioIoctlCodes.SetFormat, PackFormat(new PcmFormat(96000, 16, 2)));
        Check.Status(result, NtStatus.InvalidDeviceState);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Position_advances_with_the_fake_clock_and_reads_produce_sine()
    {
        var (host, device) = await StartHostAsync();

        // Switch to 48000 Hz then start.
        await device.SendIoctlAsync(AudioIoctlCodes.SetFormat, PackFormat(new PcmFormat(48000, 16, 2)));
        await device.SendIoctlAsync(AudioIoctlCodes.Start);

        host.FakeTime!.Advance(TimeSpan.FromSeconds(1));

        var position = await device.SendIoctlAsync(AudioIoctlCodes.GetPosition, outputSize: 20);
        Check.Success(position);
        Check.Bytes(position, 20);
        var bytePosition = BinaryPrimitives.ReadUInt64LittleEndian(position.Output);
        var framePosition = BinaryPrimitives.ReadUInt64LittleEndian(position.Output.AsSpan(8));
        Assert.Equal(48000ul, framePosition);
        Assert.Equal(48000ul * 2ul * (ulong)sizeof(short), bytePosition);

        // Read 0.5 seconds of audio: 24000 frames × 4 bytes = 96000 bytes.
        var audio = await device.ReadAsync(96000);
        Check.Success(audio);
        Check.Bytes(audio, 96000);

        // Signal is a real sine: some peaks well above zero, nothing clipping, channels equal (stereo dupe).
        short peak = 0;
        for (var i = 0; i + 2 <= audio.BytesTransferred; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(audio.Output.AsSpan(i));
            peak = Math.Max(peak, Math.Abs(sample));
        }
        Assert.InRange(peak, 5000, (short)(0.25 * short.MaxValue + 2)); // amp 0.25

        // Stopping must not change the position, and reads still work (buffer content).
        await device.SendIoctlAsync(AudioIoctlCodes.Stop);
        var afterStop = await device.SendIoctlAsync(AudioIoctlCodes.GetPosition, outputSize: 20);
        Assert.Equal(framePosition, BinaryPrimitives.ReadUInt64LittleEndian(afterStop.Output.AsSpan(8)));

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Volume_gain_applies_to_output()
    {
        var (host, device) = await StartHostAsync();
        await device.SendIoctlAsync(AudioIoctlCodes.SetFormat, PackFormat(new PcmFormat(48000, 16, 2)));
        await device.SendIoctlAsync(AudioIoctlCodes.Start);
        host.FakeTime!.Advance(TimeSpan.FromMilliseconds(500));

        var full = await ReadPeak(device);
        Assert.True(full > 5000, $"expected audible signal, peak={full}");

        // Set master gain to 0.5, mute off, no ramp, master-only.
        var volume = new byte[17];
        BinaryPrimitives.WriteSingleLittleEndian(volume.AsSpan(0), 0.5f);
        volume[8] = 0;                       // not muted
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(9), 0);  // rampMs
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(13), 0); // channelCount
        await device.SendIoctlAsync(AudioIoctlCodes.SetVolume, volume);

        host.FakeTime.Advance(TimeSpan.FromMilliseconds(500));
        var halved = await ReadPeak(device);
        Assert.InRange(halved, full * 4 / 10, full * 6 / 10);

        // Mute produces silence.
        volume[8] = 1;
        await device.SendIoctlAsync(AudioIoctlCodes.SetVolume, volume);
        host.FakeTime.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, await ReadPeak(device));

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Supported_formats_ioctl_lists_the_catalog()
    {
        var (host, device) = await StartHostAsync();
        var result = await device.SendIoctlAsync(AudioIoctlCodes.GetSupportedFormats, outputSize: 4 + 3 * 12);
        Check.Success(result);
        Check.Bytes(result, 4 + 3 * 12);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(result.Output));
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(result.Output.AsSpan(4)));
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Sample_specific_ioctl_extends_the_contract()
    {
        var (host, device) = await StartHostAsync();

        var get = await device.SendIoctlAsync(SampleAudioIoctlCodes.GetToneFrequency, outputSize: sizeof(float));
        Check.Success(get);
        Assert.Equal(440f, BinaryPrimitives.ReadSingleLittleEndian(get.Output));

        var set = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(set, 1000f);
        Check.Success(await device.SendIoctlAsync(SampleAudioIoctlCodes.SetToneFrequency, set));

        var after = await device.SendIoctlAsync(SampleAudioIoctlCodes.GetToneFrequency, outputSize: sizeof(float));
        Assert.Equal(1000f, BinaryPrimitives.ReadSingleLittleEndian(after.Output));

        BinaryPrimitives.WriteSingleLittleEndian(set, 0f); // below 20 Hz
        var bad = await device.SendIoctlAsync(SampleAudioIoctlCodes.SetToneFrequency, set);
        Check.Status(bad, NtStatus.InvalidParameter);

        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Unknown_ioctl_rejects_with_invalid_device_request()
    {
        var (host, device) = await StartHostAsync();
        var result = await device.SendIoctlAsync(WinIoctl.CtlCode(WinIoctl.FileDeviceSound, 0x799, WinIoctl.MethodBuffered, WinIoctl.FileAccessAny));
        Check.Status(result, NtStatus.InvalidDeviceRequest);
        await host.ShutdownAsync();
    }

    [Fact]
    public void Manifest_generates_a_valid_inf()
    {
        var manifestPath = FindManifest();
        var manifest = DriverManifest.FromJson(File.ReadAllText(manifestPath));
        var inf = new WindowsDriver.InfGen.InfGenerator(new DateOnly(2026, 11, 18)).Generate(manifest);

        Assert.Contains("Class=Media", inf);
        Assert.Contains("UmdfService=WdsVirtualAudio", inf);
        Assert.Contains(@"Root\ContosoVirtualAudio", inf);

        var issues = new WindowsDriver.InfGen.InfValidator().Validate(inf);
        Assert.True(issues.Count == 0, "INF issues: " + string.Join("; ", issues.Select(i => $"[{i.Section}] {i.Message}")));
    }

    private static async Task<short> ReadPeak(TestDevice device)
    {
        var audio = await device.ReadAsync(9600);
        Check.Success(audio);
        short peak = 0;
        for (var i = 0; i + 2 <= audio.BytesTransferred; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(audio.Output.AsSpan(i));
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return peak;
    }

    private static string FindManifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "../../../../manifest.json");
        return File.Exists(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), "manifest.json");
    }
}
