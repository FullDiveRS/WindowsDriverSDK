using System.Runtime.InteropServices;
using WindowsDriver.Sdk;

namespace WindowsDriver.Interop.Kmdf;

/// <summary>
/// Wire contract between the managed KMDF control plane
/// (<see cref="KmdfControlPlane"/>) and the KMDF shim driver (shim/WdsKmdfShim).
/// All payloads are METHOD_BUFFERED little-endian; the C side mirrors every
/// struct in wds_shim_protocol.h.
/// </summary>
public static class KmdfShimProtocol
{
    /// <summary>Response magic for PING: the ASCII bytes "WSHI" read little-endian.</summary>
    public const uint Magic = 0x49485357; // 'W','S','H','I'

    public const uint ShimVersionMajor = 1;
    public const uint ShimVersionMinor = 0;

    private static uint Ctl(uint function, uint access) =>
        WinIoctl.VendorControlCode(function, access);

    /// <summary>Out: u32 magic — liveness check.</summary>
    public static readonly uint Ping = Ctl(0x800, WinIoctl.FileAccessRead);

    /// <summary>Out: (u32 major, u32 minor).</summary>
    public static readonly uint GetVersion = Ctl(0x801, WinIoctl.FileAccessRead);

    /// <summary>
    /// In: KmdfCommandHeader + command data.
    /// Out: KmdfResultHeader + result data.
    /// </summary>
    public static readonly uint SendCommand = Ctl(0x802, WinIoctl.FileAccessReadWrite);

    /// <summary>Out: (u32 count) + count × KmdfEventRecord. Empty when the ring has no events.</summary>
    public static readonly uint ReadEvents = Ctl(0x803, WinIoctl.FileAccessRead);

    /// <summary>In: u32 eventMask (0 = all). Marks events consumed by the caller.</summary>
    public static readonly uint AcknowledgeEvents = Ctl(0x804, WinIoctl.FileAccessWrite);
}

/// <summary>Header of a command sent to the shim via SendCommand (data follows).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct KmdfCommandHeader
{
    public uint CommandId;
    public uint DataSize;
    public const int WireSize = sizeof(uint) + sizeof(uint);
}

/// <summary>Header of the result returned by SendCommand (data follows).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct KmdfResultHeader
{
    public uint Status;      // NTSTATUS
    public uint DataSize;
    public const int WireSize = sizeof(uint) + sizeof(uint);
}

/// <summary>Header of one event record returned by ReadEvents (data follows).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct KmdfEventRecord
{
    public ulong Sequence;
    public uint EventId;
    public uint DataSize;
    public const int WireSize = sizeof(ulong) + sizeof(uint) + sizeof(uint);

    /// <summary>Total wire size of a record with the given data length.</summary>
    public static int TotalSize(int dataSize) => WireSize + dataSize;
}

/// <summary>One parsed event from the shim's ring buffer.</summary>
public sealed record KmdfEvent(ulong Sequence, uint EventId, byte[] Data);

/// <summary>Standard command IDs the shim understands (extend for your device).</summary>
public static class KmdfCommandIds
{
    /// <summary>Vendor-defined commands start here (0..0xFF are reserved for the shim itself).</summary>
    public const uint FirstVendorCommand = 0x100;
}
