namespace WindowsDriver.Sdk.Serial;

/// <summary>Parity modes (matches SERIAL parity settings).</summary>
public enum SerialParity : byte
{
    None = 0,
    Odd = 1,
    Even = 2,
    Mark = 3,
    Space = 4,
}

/// <summary>Stop bits. OnePointFive is only valid with 5 data bits (classic 8250 rule).</summary>
public enum SerialStopBits : byte
{
    One = 0,
    OnePointFive = 1,
    Two = 2,
}

/// <summary>Flow control modes.</summary>
public enum SerialFlowControl : byte
{
    None = 0,
    XonXoff = 1,
    RtsCts = 2,
}

/// <summary>
/// The serial line configuration. Validation follows the classic UART rules:
/// 5–8 data bits, baud 50..1,000,000, and 1.5 stop bits only with 5 data bits.
/// </summary>
public sealed record SerialLineConfig(
    uint BaudRate = 115_200,
    byte DataBits = 8,
    SerialParity Parity = SerialParity.None,
    SerialStopBits StopBits = SerialStopBits.One,
    SerialFlowControl FlowControl = SerialFlowControl.None)
{
    public const uint MinBaudRate = 50;
    public const uint MaxBaudRate = 1_000_000;

    /// <summary>Wired size of the config struct in IOCTL payloads.</summary>
    public const int WireSize = 8;

    /// <summary>Writes this config as (u32 baud, u8 dataBits, u8 parity, u8 stopBits, u8 flow, pad).</summary>
    public void WriteTo(Span<byte> dest)
    {
        WireFormat.ThrowIfTooSmall(dest.Length, 0, WireSize);
        WireFormat.WriteUInt32(dest, 0, BaudRate);
        dest[4] = DataBits;
        dest[5] = (byte)Parity;
        dest[6] = (byte)StopBits;
        dest[7] = (byte)FlowControl;
    }

    public static SerialLineConfig ReadFrom(ReadOnlySpan<byte> src)
    {
        WireFormat.ThrowIfTooSmall(src.Length, 0, WireSize);
        return new SerialLineConfig(
            WireFormat.ReadUInt32(src, 0),
            src[4],
            (SerialParity)src[5],
            (SerialStopBits)src[6],
            (SerialFlowControl)src[7]);
    }

    /// <summary>Throws <see cref="DriverException"/> (InvalidParameter) when the combination is invalid.</summary>
    public void Validate()
    {
        if (BaudRate < MinBaudRate || BaudRate > MaxBaudRate)
        {
            throw new DriverException(NtStatus.InvalidParameter, $"Baud rate {BaudRate} is outside [{MinBaudRate}, {MaxBaudRate}].");
        }
        if (DataBits is < 5 or > 8)
        {
            throw new DriverException(NtStatus.InvalidParameter, $"Data bits must be 5..8, got {DataBits}.");
        }
        if (StopBits == SerialStopBits.OnePointFive && DataBits != 5)
        {
            throw new DriverException(NtStatus.InvalidParameter, "1.5 stop bits are only valid with 5 data bits.");
        }
        if (StopBits == SerialStopBits.Two && DataBits == 5)
        {
            throw new DriverException(NtStatus.InvalidParameter, "5 data bits cannot be combined with 2 stop bits.");
        }
        if (!Enum.IsDefined(Parity))
        {
            throw new DriverException(NtStatus.InvalidParameter, $"Unknown parity value {(byte)Parity}.");
        }
        if (!Enum.IsDefined(FlowControl))
        {
            throw new DriverException(NtStatus.InvalidParameter, $"Unknown flow control value {(byte)FlowControl}.");
        }
    }
}
