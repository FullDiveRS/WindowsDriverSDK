using System.Buffers.Binary;

namespace WindowsDriver.Sdk;

/// <summary>
/// Explicit little-endian read/write helpers for IOCTL payloads. Windows runs on
/// little-endian hardware, so every wire struct in the SDK is little-endian.
/// These helpers throw <see cref="DriverException"/> with
/// <see cref="NtStatus.BufferTooSmall"/> when the destination is too short —
/// the same status a real driver returns for undersized output buffers.
/// </summary>
public static class WireFormat
{
    public static void WriteInt16(Span<byte> dest, int offset, short value) { ThrowIfTooSmall(dest.Length, offset, sizeof(short)); BinaryPrimitives.WriteInt16LittleEndian(dest.Slice(offset), value); }
    public static void WriteUInt16(Span<byte> dest, int offset, ushort value) { ThrowIfTooSmall(dest.Length, offset, sizeof(ushort)); BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(offset), value); }
    public static void WriteInt32(Span<byte> dest, int offset, int value) { ThrowIfTooSmall(dest.Length, offset, sizeof(int)); BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(offset), value); }
    public static void WriteUInt32(Span<byte> dest, int offset, uint value) { ThrowIfTooSmall(dest.Length, offset, sizeof(uint)); BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(offset), value); }
    public static void WriteInt64(Span<byte> dest, int offset, long value) { ThrowIfTooSmall(dest.Length, offset, sizeof(long)); BinaryPrimitives.WriteInt64LittleEndian(dest.Slice(offset), value); }
    public static void WriteUInt64(Span<byte> dest, int offset, ulong value) { ThrowIfTooSmall(dest.Length, offset, sizeof(ulong)); BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(offset), value); }
    public static void WriteFloat(Span<byte> dest, int offset, float value) { ThrowIfTooSmall(dest.Length, offset, sizeof(float)); BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(offset), value); }
    public static void WriteDouble(Span<byte> dest, int offset, double value) { ThrowIfTooSmall(dest.Length, offset, sizeof(double)); BinaryPrimitives.WriteDoubleLittleEndian(dest.Slice(offset), value); }

    public static short ReadInt16(ReadOnlySpan<byte> src, int offset) { ThrowIfTooSmall(src.Length, offset, sizeof(short)); return BinaryPrimitives.ReadInt16LittleEndian(src.Slice(offset)); }
    public static ushort ReadUInt16(ReadOnlySpan<byte> src, int offset) { ThrowIfTooSmall(src.Length, offset, sizeof(ushort)); return BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(offset)); }
    public static int ReadInt32(ReadOnlySpan<byte> src, int offset) { ThrowIfTooSmall(src.Length, offset, sizeof(int)); return BinaryPrimitives.ReadInt32LittleEndian(src.Slice(offset)); }
    public static uint ReadUInt32(ReadOnlySpan<byte> src, int offset) { ThrowIfTooSmall(src.Length, offset, sizeof(uint)); return BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(offset)); }
    public static long ReadInt64(ReadOnlySpan<byte> src, int offset) { ThrowIfTooSmall(src.Length, offset, sizeof(long)); return BinaryPrimitives.ReadInt64LittleEndian(src.Slice(offset)); }
    public static ulong ReadUInt64(ReadOnlySpan<byte> src, int offset) { ThrowIfTooSmall(src.Length, offset, sizeof(ulong)); return BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(offset)); }
    public static float ReadFloat(ReadOnlySpan<byte> src, int offset) { ThrowIfTooSmall(src.Length, offset, sizeof(float)); return BinaryPrimitives.ReadSingleLittleEndian(src.Slice(offset)); }
    public static double ReadDouble(ReadOnlySpan<byte> src, int offset) { ThrowIfTooSmall(src.Length, offset, sizeof(double)); return BinaryPrimitives.ReadDoubleLittleEndian(src.Slice(offset)); }

    /// <summary>Throws BufferTooSmall when [offset, offset + size) falls outside the buffer.</summary>
    public static void ThrowIfTooSmall(int bufferLength, int offset, int size)
    {
        if (offset < 0 || size < 0 || (long)offset + size > bufferLength)
        {
            throw new DriverException(NtStatus.BufferTooSmall,
                $"Need {size} bytes at offset {offset} but the buffer is {bufferLength} bytes.");
        }
    }
}
