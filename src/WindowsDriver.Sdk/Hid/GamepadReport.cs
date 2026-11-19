using System.Buffers.Binary;

namespace WindowsDriver.Sdk.Hid;

/// <summary>
/// Driver-side gamepad state; converted to a wire report via
/// <see cref="GamepadInputReport.Pack"/>. Buttons are 1-based (physical button
/// numbers 1..12) matching the descriptor's Usage Min/Max.
/// </summary>
public sealed class GamepadState
{
    public const int ButtonCount = 12;

    private readonly bool[] _buttons = new bool[ButtonCount + 1];

    /// <summary>16-bit accelerometers/gyros in device units.</summary>
    public short X { get; set; }
    public short Y { get; set; }
    public short Z { get; set; }
    public short Rx { get; set; }
    public short Ry { get; set; }
    public short Rz { get; set; }

    public bool this[int button]
    {
        get
        {
            if (button < 1 || button > ButtonCount) throw new ArgumentOutOfRangeException(nameof(button), "Buttons are numbered 1..12.");
            return _buttons[button];
        }
        set
        {
            if (button < 1 || button > ButtonCount) throw new ArgumentOutOfRangeException(nameof(button), "Buttons are numbered 1..12.");
            _buttons[button] = value;
        }
    }

    public void SetButtons(params int[] buttons)
    {
        Array.Clear(_buttons);
        foreach (var b in buttons)
        {
            this[b] = true;
        }
    }
}

/// <summary>
/// Packs/unpacks the gamepad input report (report ID 1) against the canonical
/// <see cref="HidDescriptors.GamepadSixAxisTwelveButtons"/> descriptor:
/// <code>
/// byte 0        report ID (1)
/// bytes 1..2    12 button bits (LSB first) + 4 padding bits
/// bytes 3..14   X, Y, Z, Rx, Ry, Rz as int16 little-endian
/// </code>
/// </summary>
public static class GamepadInputReport
{
    public const byte ReportId = 1;
    public const int ReportSizeBytes = 15; // 1 + 2 + 12

    public static byte[] Pack(GamepadState state)
    {
        var report = new byte[ReportSizeBytes];
        report[0] = ReportId;

        ushort buttons = 0;
        for (var b = 1; b <= GamepadState.ButtonCount; b++)
        {
            if (state[b])
            {
                buttons |= (ushort)(1 << (b - 1));
            }
        }
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(1), buttons);

        WriteAxis(report, 3, state.X);
        WriteAxis(report, 5, state.Y);
        WriteAxis(report, 7, state.Z);
        WriteAxis(report, 9, state.Rx);
        WriteAxis(report, 11, state.Ry);
        WriteAxis(report, 13, state.Rz);
        return report;
    }

    public static GamepadState Unpack(ReadOnlySpan<byte> report)
    {
        if (report.Length != ReportSizeBytes)
        {
            throw new ArgumentException($"Gamepad input report must be exactly {ReportSizeBytes} bytes.", nameof(report));
        }
        if (report[0] != ReportId)
        {
            throw new ArgumentException($"Report ID must be {ReportId}, got {report[0]}.", nameof(report));
        }
        var state = new GamepadState
        {
            X = BinaryPrimitives.ReadInt16LittleEndian(report.Slice(3)),
            Y = BinaryPrimitives.ReadInt16LittleEndian(report.Slice(5)),
            Z = BinaryPrimitives.ReadInt16LittleEndian(report.Slice(7)),
            Rx = BinaryPrimitives.ReadInt16LittleEndian(report.Slice(9)),
            Ry = BinaryPrimitives.ReadInt16LittleEndian(report.Slice(11)),
            Rz = BinaryPrimitives.ReadInt16LittleEndian(report.Slice(13)),
        };
        var buttons = BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(1));
        for (var b = 1; b <= GamepadState.ButtonCount; b++)
        {
            state[b] = (buttons & (1 << (b - 1))) != 0;
        }
        return state;
    }

    private static void WriteAxis(byte[] report, int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(offset), value);
}
