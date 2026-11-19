using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsDriver.Interop.Win32;

/// <summary>
/// The Win32 P/Invoke surface used by the KMDF control plane. Real declarations
/// (kernel32.dll), safe-handle based, usable from any .NET process.
/// </summary>
public static class Win32Native
{
    // CreateFile access/share/disposition flags (winbase.h)
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;

    /// <summary>Opens a device (\\.\Name or the symbolic link from CreateDeviceInterface).</summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>Sends a buffered IOCTL to an opened device handle.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>
    /// Sends a buffered IOCTL and throws <see cref="Win32Exception"/> on failure;
    /// returns the bytes written to the output buffer.
    /// </summary>
    public static int SendIoctl(SafeFileHandle device, uint controlCode, ReadOnlySpan<byte> input, Span<byte> output)
    {
        var inPtr = IntPtr.Zero;
        var outPtr = IntPtr.Zero;
        try
        {
            if (input.Length > 0)
            {
                inPtr = Marshal.AllocHGlobal(input.Length);
                Marshal.Copy(input.ToArray(), 0, inPtr, input.Length);
            }
            if (output.Length > 0)
            {
                outPtr = Marshal.AllocHGlobal(output.Length);
            }
            var ok = DeviceIoControl(device, controlCode, inPtr, (uint)input.Length, outPtr, (uint)output.Length, out var returned, IntPtr.Zero);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"DeviceIoControl(0x{controlCode:X8}) failed.");
            }
            var copied = Math.Min((int)returned, output.Length);
            if (copied > 0)
            {
                var managed = new byte[copied];
                Marshal.Copy(outPtr, managed, 0, copied);
                managed.CopyTo(output);
            }
            return copied;
        }
        finally
        {
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (outPtr != IntPtr.Zero) Marshal.FreeHGlobal(outPtr);
        }
    }
}
