namespace WindowsDriver.Sdk.Serial;

/// <summary>Receives every byte transmitted by a serial device (used to cross-connect two ports).</summary>
public delegate void TxSink(ReadOnlySpan<byte> data);

/// <summary>
/// SDK-level IOCTL contract for serial devices handled by <see cref="SerialDevice"/>.
/// When a serial driver is integrated with Windows, serenum/the serial class maps
/// device traffic onto the standard serial IOCTLs (serial.h); see docs/serial-guide.md.
/// </summary>
public static class SerialIoctlCodes
{
    private static uint Ctl(uint function, uint access) => WinIoctl.VendorControlCode(function, access, WinIoctl.FileDeviceSerialPort);

    /// <summary>Out: <see cref="SerialLineConfig"/> wire struct.</summary>
    public static readonly uint GetLineConfig = Ctl(0x800, WinIoctl.FileAccessRead);

    /// <summary>In: <see cref="SerialLineConfig"/> wire struct (8 bytes).</summary>
    public static readonly uint SetLineConfig = Ctl(0x801, WinIoctl.FileAccessWrite);

    /// <summary>In: bytes to transmit (any length ≥ 1).</summary>
    public static readonly uint Transmit = Ctl(0x802, WinIoctl.FileAccessWrite);

    /// <summary>In: u32 flags — bit 0 purge RX, bit 1 purge TX.</summary>
    public static readonly uint Purge = Ctl(0x803, WinIoctl.FileAccessWrite);

    /// <summary>Out: (u64 txBytes, u64 rxBytes, u32 framingErrors, u8 cts, u8 dsr, u8 breakActive, u8 loopback).</summary>
    public static readonly uint GetStatus = Ctl(0x804, WinIoctl.FileAccessRead);

    /// <summary>Out: u32 receive queue depth (bytes waiting).</summary>
    public static readonly uint GetRxCount = Ctl(0x805, WinIoctl.FileAccessRead);
}

/// <summary>Configuration for <see cref="SerialDevice"/>.</summary>
public sealed class SerialDeviceOptions
{
    public SerialLineConfig InitialConfig { get; init; } = new();

    /// <summary>When true, transmitted bytes echo into the RX queue (loopback plug).</summary>
    public bool Loopback { get; init; } = true;

    public int RxQueueBytes { get; init; } = 4096;
    public int TxQueueBytes { get; init; } = 4096;
}

/// <summary>
/// A complete virtual serial port: line config with UART validation, TX/RX
/// queues, loopback, modem status (CTS/DSR), break detection and counters.
/// Two instances can be cross-connected with <see cref="SerialLoopbackPair"/>
/// to form a virtual null-modem cable.
/// </summary>
public class SerialDevice : DeviceBase
{
    private readonly ByteRingBuffer _rx;
    private readonly ByteRingBuffer _tx;
    private readonly object _gate = new();

    public SerialDevice(DeviceContext context, SerialDeviceOptions? options = null)
        : base(context)
    {
        Options = options ?? new SerialDeviceOptions();
        Options.InitialConfig.Validate();
        Config = Options.InitialConfig;
        _rx = new ByteRingBuffer(Options.RxQueueBytes);
        _tx = new ByteRingBuffer(Options.TxQueueBytes);

        DefaultQueue
            .OnDeviceControl(HandleDeviceControlAsync)
            .OnRead(HandleReadAsync)
            .OnWrite(HandleWriteAsync);
    }

    public SerialDeviceOptions Options { get; }
    public SerialLineConfig Config { get; private set; }
    public ByteRingBuffer RxQueue => _rx;
    public ByteRingBuffer TxQueue => _tx;

    public bool Cts { get; private set; } = true;
    public bool Dsr { get; private set; } = true;
    public bool BreakActive { get; private set; }

    public long FramingErrors { get; private set; }

    /// <summary>Set by <see cref="SerialLoopbackPair"/> to receive every transmitted byte.</summary>
    internal TxSink? TxSubscription { get; set; }

    public override Task OnPrepareHardwareAsync(CancellationToken cancellationToken)
    {
        Context.Log.Info("serial port ready: {0} baud / {1}{2}{3} / loopback={4}",
            Config.BaudRate, Config.DataBits, Config.Parity, Config.StopBits, Options.Loopback);
        return Task.CompletedTask;
    }

    /// <summary>Applies and validates a new line configuration.</summary>
    public void ApplyConfig(SerialLineConfig config)
    {
        config.Validate();
        Config = config;
        Context.Log.Info("serial config applied: {0}", config);
    }

    /// <summary>Injects bytes directly into the RX queue (simulates a remote peer).</summary>
    public int InjectReceive(ReadOnlySpan<byte> data) => _rx.Write(data);

    /// <summary>
    /// Transmits bytes immediately: into the TX queue, back on RX when loopback
    /// is enabled, and to the paired device when cross-connected.
    /// </summary>
    public int Transmit(ReadOnlySpan<byte> data)
    {
        var accepted = _tx.Write(data);
        if (Options.Loopback)
        {
            _rx.Write(data.Slice(0, accepted));
        }
        TxSubscription?.Invoke(data.Slice(0, accepted));
        return accepted;
    }

    /// <summary>Simulates a framing error on the next received byte(s).</summary>
    public void InjectFramingError() { lock (_gate) { FramingErrors++; } }

    /// <summary>Simulates a break condition (line held at space).</summary>
    public void AssertBreak(bool active)
    {
        BreakActive = active;
        if (active)
        {
            InjectFramingError();
        }
    }

    private Task HandleReadAsync(IoRequest request, CancellationToken ct)
    {
        var read = _rx.Read(request.OutputBuffer.Span);
        request.Complete(NtStatus.Success, read);
        return Task.CompletedTask;
    }

    private Task HandleWriteAsync(IoRequest request, CancellationToken ct)
    {
        var accepted = _tx.Write(request.InputBuffer.Span);
        if (Options.Loopback)
        {
            // Loopback plug: whatever we transmitted comes back on RX.
            _rx.Write(request.InputBuffer.Span.Slice(0, accepted));
        }
        TxSubscription?.Invoke(request.InputBuffer.Span.Slice(0, accepted));
        request.Complete(NtStatus.Success, accepted);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the <see cref="SerialIoctlCodes"/> contract. Subclasses override
    /// this to add device-specific IOCTLs and fall back to base for the rest.
    /// </summary>
    protected virtual Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        var code = request.IoControlCode;
        if (code == SerialIoctlCodes.GetLineConfig)
        {
            if (request.OutputBuffer.Length < SerialLineConfig.WireSize)
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                Config.WriteTo(request.OutputBuffer.Span);
                request.Complete(NtStatus.Success, SerialLineConfig.WireSize);
            }
        }
        else if (code == SerialIoctlCodes.SetLineConfig)
        {
            if (request.InputBuffer.Length < SerialLineConfig.WireSize)
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                ApplyConfig(SerialLineConfig.ReadFrom(request.InputBuffer.Span));
                request.Complete(NtStatus.Success, SerialLineConfig.WireSize);
            }
        }
        else if (code == SerialIoctlCodes.Transmit)
        {
            if (request.InputBuffer.Length == 0)
            {
                request.Fail(NtStatus.InvalidParameter);
            }
            else
            {
                Transmit(request.InputBuffer.Span);
                request.Complete(NtStatus.Success, request.InputBuffer.Length);
            }
        }
        else if (code == SerialIoctlCodes.Purge)
        {
            if (request.InputBuffer.Length < sizeof(uint))
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                var flags = WireFormat.ReadUInt32(request.InputBuffer.Span, 0);
                if ((flags & 0x1) != 0)
                {
                    _rx.Clear();
                }
                if ((flags & 0x2) != 0)
                {
                    _tx.Clear();
                }
                request.Complete(NtStatus.Success, sizeof(uint));
            }
        }
        else if (code == SerialIoctlCodes.GetStatus)
        {
            var needed = 2 * sizeof(ulong) + sizeof(uint) + 4 * sizeof(byte);
            if (request.OutputBuffer.Length < needed)
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                var span = request.OutputBuffer.Span;
                long framing;
                lock (_gate)
                {
                    framing = FramingErrors;
                }
                WireFormat.WriteUInt64(span, 0, (ulong)_tx.TotalBytesWritten);
                WireFormat.WriteUInt64(span, 8, (ulong)_rx.TotalBytesRead);
                WireFormat.WriteUInt32(span, 16, (uint)framing);
                span[20] = Cts ? (byte)1 : (byte)0;
                span[21] = Dsr ? (byte)1 : (byte)0;
                span[22] = BreakActive ? (byte)1 : (byte)0;
                span[23] = Options.Loopback ? (byte)1 : (byte)0;
                request.Complete(NtStatus.Success, needed);
            }
        }
        else if (code == SerialIoctlCodes.GetRxCount)
        {
            if (request.OutputBuffer.Length < sizeof(uint))
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                WireFormat.WriteUInt32(request.OutputBuffer.Span, 0, (uint)_rx.Count);
                request.Complete(NtStatus.Success, sizeof(uint));
            }
        }
        else
        {
            request.Fail(NtStatus.InvalidDeviceRequest);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Cross-connects two <see cref="SerialDevice"/>s like a null-modem cable:
/// A's transmit goes to B's receive and vice versa. Both devices must be
/// created with <see cref="SerialDeviceOptions.Loopback"/> = false.
/// </summary>
public sealed class SerialLoopbackPair
{
    public SerialLoopbackPair(SerialDevice a, SerialDevice b)
    {
        if (a.Options.Loopback || b.Options.Loopback)
        {
            throw new InvalidOperationException("Loopback must be disabled on both devices to form a pair.");
        }
        A = a;
        B = b;
        a.TxSubscription = data => b.RxQueue.Write(data);
        b.TxSubscription = data => a.RxQueue.Write(data);
    }

    public SerialDevice A { get; }
    public SerialDevice B { get; }
}
