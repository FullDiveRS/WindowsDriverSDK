using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using WindowsDriver.Interop.Win32;
using WindowsDriver.Sdk;

namespace WindowsDriver.Interop.Kmdf;

/// <summary>
/// Managed control plane for a KMDF shim driver. Opens the device interface,
/// sends buffered IOCTLs (ping/version/commands) and drains the shim's event
/// ring. Runs in any .NET process — a service, a tray app, or tests.
///
/// On a machine without the shim device installed, Open() fails with the real
/// Win32 error (ERROR_FILE_NOT_FOUND = 2); unit tests exercise the wire
/// structures and IOCTL math without a device.
/// </summary>
public sealed class KmdfControlPlane : IDisposable
{
    private SafeFileHandle? _device;

    /// <summary>
    /// Creates a control plane for the device at <paramref name="devicePath"/>
    /// (e.g. "\\\\?\\WdsKmdfShim#..." from the shim's CreateDeviceInterface, or
    /// "\\\\.\\WdsKmdfShim0" for a named device object).
    /// </summary>
    public KmdfControlPlane(string devicePath)
    {
        DevicePath = devicePath;
    }

    public string DevicePath { get; }

    public bool IsOpen => _device is { IsInvalid: false, IsClosed: false };

    /// <summary>Opens the device handle. Throws System.ComponentModel.Win32Exception on failure.</summary>
    public void Open()
    {
        var handle = Win32Native.CreateFileW(
            DevicePath,
            Win32Native.GenericRead | Win32Native.GenericWrite,
            Win32Native.FileShareRead | Win32Native.FileShareWrite,
            lpSecurityAttributes: IntPtr.Zero,
            Win32Native.OpenExisting,
            dwFlagsAndAttributes: 0,
            hTemplateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                $"Cannot open device '{DevicePath}'. Is the shim driver installed?");
        }
        _device = handle;
    }

    /// <summary>Sends PING and verifies the shim magic.</summary>
    public void Ping()
    {
        var device = RequireOpen();
        var buffer = new byte[sizeof(uint)];
        var written = Win32Native.SendIoctl(device, KmdfShimProtocol.Ping, ReadOnlySpan<byte>.Empty, buffer);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (written < sizeof(uint) || magic != KmdfShimProtocol.Magic)
        {
            throw new InvalidOperationException($"Shim responded with unexpected magic 0x{magic:X8}.");
        }
    }

    /// <summary>Returns the shim protocol version.</summary>
    public (uint Major, uint Minor) GetVersion()
    {
        var device = RequireOpen();
        var buffer = new byte[2 * sizeof(uint)];
        Win32Native.SendIoctl(device, KmdfShimProtocol.GetVersion, ReadOnlySpan<byte>.Empty, buffer);
        return (BinaryPrimitives.ReadUInt32LittleEndian(buffer), BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)));
    }

    /// <summary>Sends a command; returns the result status and payload.</summary>
    public (NtStatus Status, byte[] Data) SendCommand(uint commandId, ReadOnlySpan<byte> commandData)
    {
        var device = RequireOpen();
        var input = new byte[KmdfCommandHeader.WireSize + commandData.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(input, commandId);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), (uint)commandData.Length);
        commandData.CopyTo(input.AsSpan(KmdfCommandHeader.WireSize));

        var output = new byte[KmdfResultHeader.WireSize + 4096];
        var written = Win32Native.SendIoctl(device, KmdfShimProtocol.SendCommand, input, output);
        if (written < KmdfResultHeader.WireSize)
        {
            throw new InvalidOperationException("SendCommand returned a truncated result header.");
        }
        var status = (NtStatus)BinaryPrimitives.ReadUInt32LittleEndian(output);
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4));
        var data = new byte[Math.Min((int)dataSize, written - KmdfResultHeader.WireSize)];
        output.AsSpan(KmdfResultHeader.WireSize, data.Length).CopyTo(data);
        return (status, data);
    }

    /// <summary>Drains up to <paramref name="maxEvents"/> events from the shim ring.</summary>
    public IReadOnlyList<KmdfEvent> ReadEvents(int maxEvents = 64)
    {
        var device = RequireOpen();
        var buffer = new byte[sizeof(uint) + maxEvents * KmdfEventRecord.WireSize + 4096];
        var written = Win32Native.SendIoctl(device, KmdfShimProtocol.ReadEvents, ReadOnlySpan<byte>.Empty, buffer);
        if (written < sizeof(uint))
        {
            return Array.Empty<KmdfEvent>();
        }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var events = new List<KmdfEvent>((int)count);
        var offset = sizeof(uint);
        for (var i = 0; i < count && offset + KmdfEventRecord.WireSize <= written; i++)
        {
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset));
            var eventId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 8));
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 12));
            offset += KmdfEventRecord.WireSize;
            var data = new byte[Math.Min((int)dataSize, Math.Max(0, written - offset))];
            buffer.AsSpan(offset, data.Length).CopyTo(data);
            offset += data.Length;
            events.Add(new KmdfEvent(sequence, eventId, data));
        }
        return events;
    }

    private SafeFileHandle RequireOpen() =>
        _device ?? throw new InvalidOperationException("Call Open() first.");

    public void Dispose()
    {
        _device?.Dispose();
        _device = null;
    }
}
