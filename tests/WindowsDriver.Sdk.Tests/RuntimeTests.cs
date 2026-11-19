using System.Buffers.Binary;
using WindowsDriver.Sdk;
using WindowsDriver.Sdk.Audio;
using WindowsDriver.Testing;
using Xunit;

namespace WindowsDriver.Sdk.Tests;

public class RuntimeTests
{
    // ----- NtStatus -----

    [Theory]
    [InlineData(NtStatus.Success, true, false, false, false)]
    [InlineData(NtStatus.Pending, true, false, false, false)]
    [InlineData(NtStatus.BufferOverflow, false, false, true, false)]
    [InlineData(NtStatus.InvalidDeviceRequest, false, false, false, true)]
    [InlineData(NtStatus.AccessDenied, false, false, false, true)]
    public void Severity_fields_are_derived_from_bit_30(NtStatus status, bool success, bool info, bool warning, bool error)
    {
        Assert.Equal(success, status.IsSuccess());
        Assert.Equal(info, status.IsInformational());
        Assert.Equal(warning, status.IsWarning());
        Assert.Equal(error, status.IsError());
    }

    // ----- CTL_CODE bit math (matches the C macro) -----

    [Fact]
    public void CtlCode_matches_the_C_macro()
    {
        // #define CTL_CODE( DeviceType, Function, Method, Access ) (
        //   ((DeviceType) << 16) | ((Access) << 14) | ((Function) << 2) | (Method) )
        Assert.Equal(0x00226004u, WinIoctl.CtlCode(0x22, 0x801, WinIoctl.MethodBuffered, WinIoctl.FileAccessRead));
        Assert.Equal(0x22u, WinIoctl.DeviceTypeFromControl(0x00226004u));
        Assert.Equal(0x801u, WinIoctl.FunctionFromControl(0x00226004u));
        Assert.Equal(WinIoctl.MethodBuffered, WinIoctl.MethodFromControl(0x00226004u));
        Assert.Equal(WinIoctl.FileAccessRead, WinIoctl.AccessFromControl(0x00226004u));

        // Sound-device example from the audio contract.
        var audio = AudioIoctlCodes.GetFormat;
        Assert.Equal(WinIoctl.FileDeviceSound, WinIoctl.DeviceTypeFromControl(audio));
        Assert.Equal(0x802u, WinIoctl.FunctionFromControl(audio));
    }

    [Fact]
    public void VendorControlCode_rejects_reserved_functions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WinIoctl.VendorControlCode(0x100));
        _ = WinIoctl.VendorControlCode(0x800); // lowest allowed
    }

    // ----- WireFormat -----

    [Fact]
    public void WireFormat_roundtrips_little_endian_values()
    {
        var buffer = new byte[24];
        WireFormat.WriteInt16(buffer, 0, -12345);
        WireFormat.WriteUInt32(buffer, 2, 0xDEADBEEF);
        WireFormat.WriteInt64(buffer, 6, long.MinValue);
        WireFormat.WriteDouble(buffer, 14, Math.PI);

        Assert.Equal(-12345, WireFormat.ReadInt16(buffer, 0));
        Assert.Equal(0xDEADBEEFu, WireFormat.ReadUInt32(buffer, 2));
        Assert.Equal(long.MinValue, WireFormat.ReadInt64(buffer, 6));
        Assert.Equal(Math.PI, WireFormat.ReadDouble(buffer, 14));

        // Bytes are explicitly little-endian: 0xDEADBEEF reads as EF BE AD DE.
        Assert.Equal([0xEF, 0xBE, 0xAD, 0xDE], buffer.AsSpan(2, 4).ToArray());
    }

    [Fact]
    public void WireFormat_throws_buffer_too_small()
    {
        var ex = Assert.Throws<DriverException>(() =>
        {
            Span<byte> tiny = stackalloc byte[3];
            WireFormat.WriteInt32(tiny, 0, 1);
        });
        Assert.Equal(NtStatus.BufferTooSmall, ex.Status);
    }

    // ----- PropertyStore -----

    [Fact]
    public void PropertyStore_handles_typed_values()
    {
        var store = new PropertyStore();
        store.Set("name", "device");
        store.Set("count", 42u);
        store.Set("enabled", true);
        store.Set("id", new Guid("12345678-1234-1234-1234-123456789ABC"));

        Assert.Equal("device", store.GetString("name"));
        Assert.Equal(42u, store.GetDword("count"));
        Assert.True(store.GetBool("enabled"));
        Assert.Equal(new Guid("12345678-1234-1234-1234-123456789ABC"), store.GetGuid("id"));
        Assert.Equal("123", store.GetDword("missing-does-not-exist", defaultValue: 123).ToString());
    }

    // ----- ByteRingBuffer -----

    [Fact]
    public void RingBuffer_wraps_and_counts_drops()
    {
        var ring = new ByteRingBuffer(8);
        Assert.Equal(3, ring.Write([1, 2, 3]));
        Assert.Equal(5, ring.Write(Enumerable.Range(4, 10).Select(i => (byte)i).ToArray())); // 5 accepted, 5 dropped
        Assert.Equal(5, ring.DroppedBytes);
        Assert.Equal(8, ring.Count);

        var output = new byte[8];
        Assert.Equal(8, ring.Read(output));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], output);

        Assert.Equal(0, ring.Read(output));
        Assert.Equal(8, ring.TotalBytesWritten); // accepted bytes only (5 were dropped)
        Assert.Equal(8, ring.TotalBytesRead);
    }

    [Fact]
    public void RingBuffer_skip_and_clear_work_across_the_wrap()
    {
        var ring = new ByteRingBuffer(4);
        ring.Write([1, 2, 3, 4]);
        Assert.Equal(2, ring.Skip(2));
        var one = new byte[1];
        Assert.Equal(1, ring.Read(one));
        Assert.Equal(3, one[0]);
        ring.Clear();
        Assert.Equal(0, ring.Count);
    }

    // ----- IoQueue -----

    private static async Task<(IoRequest Request, IoResult Result)> SendAsync(IoQueue queue, uint ioctl, byte[]? input = null, int outputSize = 64)
    {
        var request = new IoRequest(IoRequestKind.DeviceControl, ioctl, input ?? Array.Empty<byte>(), new byte[outputSize], 42);
        var completion = new TaskCompletionSource<IoRequest>();
        request.Completed += completion.SetResult;
        queue.Submit(request);
        await completion.Task;
        return (request, IoResult.From(request));
    }

    [Fact]
    public async Task Queue_without_handler_rejects_the_request()
    {
        var queue = new IoQueue(NullDriverLog.Instance);
        var (_, result) = await SendAsync(queue, 0xDEAD);
        Assert.Equal(NtStatus.InvalidDeviceRequest, result.Status);
    }

    [Fact]
    public async Task Sequential_queue_runs_requests_one_at_a_time()
    {
        var queue = new IoQueue(NullDriverLog.Instance, new IoQueueOptions { Dispatch = QueueDispatchMode.Sequential });
        var concurrent = 0;
        var maxConcurrent = 0;
        var delay = TimeSpan.FromMilliseconds(20);

        queue.OnDeviceControl((request, ct) =>
        {
            Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, concurrent);
            return Task.Delay(delay).ContinueWith(_ =>
            {
                Interlocked.Decrement(ref concurrent);
                request.Complete(NtStatus.Success);
            });
        });

        var tasks = Enumerable.Range(0, 5).Select(_ => SendAsync(queue, 1)).ToArray();
        await Task.WhenAll(tasks);
        Assert.All(tasks, t => Assert.Equal(NtStatus.Success, t.Result.Result.Status));
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task DriverException_status_flows_to_the_request()
    {
        var queue = new IoQueue(NullDriverLog.Instance);
        queue.OnDeviceControl((request, ct) => throw new DriverException(NtStatus.DeviceNotReady, "not ready"));
        var (_, result) = await SendAsync(queue, 1);
        Assert.Equal(NtStatus.DeviceNotReady, result.Status);
    }

    [Fact]
    public void Double_completion_is_a_bug()
    {
        var request = new IoRequest(IoRequestKind.DeviceControl, 1, Array.Empty<byte>(), new byte[4]);
        request.Complete(NtStatus.Success, 0);
        Assert.Throws<InvalidOperationException>(() => request.Complete(NtStatus.Success, 0));
    }

    [Fact]
    public void Byte_overflow_is_rejected()
    {
        var request = new IoRequest(IoRequestKind.Read, 0, Array.Empty<byte>(), new byte[4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => request.Complete(NtStatus.Success, 5));
    }

    // ----- Manual queue -----

    [Fact]
    public async Task Manual_queue_holds_requests_until_pulled()
    {
        var queue = new IoQueue(NullDriverLog.Instance, new IoQueueOptions { Dispatch = QueueDispatchMode.Manual });
        var request = new IoRequest(IoRequestKind.DeviceControl, 7, Array.Empty<byte>(), new byte[4]);
        queue.Submit(request);
        Assert.False(request.IsCompleted);

        var pulled = await queue.ManualRequests.ReadAsync();
        Assert.Same(request, pulled);
        pulled.Complete(NtStatus.Success);
        Assert.True(request.IsCompleted);
    }

    // ----- DriverHost lifecycle -----

    private sealed class LifecycleDriver : DriverBase
    {
        public static readonly List<string> Trace = [];

        public override Task OnDriverEntryAsync(DriverContext context, CancellationToken ct)
        {
            Trace.Add("entry");
            return Task.CompletedTask;
        }

        public override Task<DeviceBase> OnDeviceAddAsync(DeviceAddContext addContext, DeviceContext deviceContext, CancellationToken ct)
        {
            Trace.Add("add");
            var device = new LifecycleDevice(deviceContext);
            return Task.FromResult<DeviceBase>(device);
        }

        public override Task OnDriverUnloadAsync()
        {
            Trace.Add("unload");
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleDevice(DeviceContext context) : DeviceBase(context)
    {
        public override Task OnPrepareHardwareAsync(CancellationToken ct)
        {
            LifecycleDriver.Trace.Add("prepare");
            return Task.CompletedTask;
        }

        public override Task OnD0EntryAsync(DevicePowerState previous, CancellationToken ct)
        {
            LifecycleDriver.Trace.Add("d0entry");
            return Task.CompletedTask;
        }

        public override Task OnD0ExitAsync(DevicePowerState next, CancellationToken ct)
        {
            LifecycleDriver.Trace.Add("d0exit");
            return Task.CompletedTask;
        }

        public override Task OnReleaseHardwareAsync(CancellationToken ct)
        {
            LifecycleDriver.Trace.Add("release");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Lifecycle_runs_in_WDF_order()
    {
        LifecycleDriver.Trace.Clear();
        var host = TestDriverHost.Builder().UseFakeTime().Build();
        await host.StartAsync<LifecycleDriver>();
        await host.AddDeviceAsync("ROOT\\TEST\\0000", "Root\\TestDevice");

        Assert.Equal(["entry", "add", "prepare", "d0entry"], LifecycleDriver.Trace);

        await host.ShutdownAsync();
        Assert.Equal(["entry", "add", "prepare", "d0entry", "d0exit", "release", "unload"], LifecycleDriver.Trace);
        Assert.False(host.IsStarted);
    }

    [Fact]
    public async Task DeviceContext_shares_the_driver_context()
    {
        var host = TestDriverHost.Builder().UseFakeTime().Build();
        await host.StartAsync<LifecycleDriver>();
        var device = await host.AddDeviceAsync("ROOT\\TEST\\0001", "Root\\TestDevice");
        Assert.Same(host.Log, device.Context.Driver.Log);
        await host.ShutdownAsync();
    }
}
