namespace WindowsDriver.Sdk;

/// <summary>
/// Device types, transfer methods and access rights used to build IOCTL codes,
/// plus <see cref="CtlCode"/>, the managed equivalent of the C macro
/// <c>CTL_CODE(DeviceType, Function, Method, Access)</c> from winioctl.h:
/// <code>
/// #define CTL_CODE(DeviceType, Function, Method, Access) \
///     (((DeviceType) &lt;&lt; 16) | ((Access) &lt;&lt; 14) | ((Function) &lt;&lt; 2) | (Method))
/// </code>
/// </summary>
public static class WinIoctl
{
    // ----- Common FILE_DEVICE_* device types (winioctl.h) -----
    public const uint FileDeviceKeyboard = 0x00000009;
    public const uint FileDeviceNetwork = 0x00000012;
    public const uint FileDeviceSerialPort = 0x0000001B;
    public const uint FileDeviceUnknown = 0x00000022;
    public const uint FileDeviceSound = 0x00000023;

    /// <summary>Recommended device type for SDK drivers that define custom IOCTLs.</summary>
    public const uint FileDeviceVendorDefault = FileDeviceUnknown;

    // ----- Transfer methods -----
    public const uint MethodBuffered = 0;
    public const uint MethodInDirect = 1;
    public const uint MethodOutDirect = 2;
    public const uint MethodNeither = 3;

    // ----- Access requirements -----
    public const uint FileAccessAny = 0x0000;    // FILE_ANY_ACCESS
    public const uint FileAccessRead = 0x0001;   // FILE_READ_ACCESS
    public const uint FileAccessWrite = 0x0002;  // FILE_WRITE_ACCESS
    public const uint FileAccessReadWrite = FileAccessRead | FileAccessWrite;

    /// <summary>
    /// IOCTL function codes below this value are reserved for Microsoft system
    /// use; vendor drivers must start their function codes here.
    /// </summary>
    public const uint FirstVendorFunction = 0x800;

    /// <summary>Managed CTL_CODE. Bit layout matches the C macro exactly.</summary>
    public static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | ((access & 0x3) << 14) | (function << 2) | method;

    /// <summary>Extracts the device-type field from an IOCTL code (bits 16–31).</summary>
    public static uint DeviceTypeFromControl(uint controlCode) => controlCode >> 16;

    /// <summary>Extracts the function field from an IOCTL code (bits 2–13, a 12-bit field).</summary>
    public static uint FunctionFromControl(uint controlCode) => (controlCode >> 2) & 0xFFF;

    /// <summary>Extracts the method field from an IOCTL code (bits 0–1).</summary>
    public static uint MethodFromControl(uint controlCode) => controlCode & 0x3;

    /// <summary>Extracts the access field from an IOCTL code (bits 14–15).</summary>
    public static uint AccessFromControl(uint controlCode) => (controlCode >> 14) & 0x3;

    /// <summary>
    /// Builds a vendor-defined IOCTL with METHOD_BUFFERED — the default choice for
    /// SDK drivers because the host copies buffers in and out for you.
    /// </summary>
    /// <param name="function">Function code, must be <see cref="FirstVendorFunction"/> or higher.</param>
    /// <param name="access">Access requirement (<see cref="FileAccessAny"/>, <see cref="FileAccessRead"/>, <see cref="FileAccessWrite"/> or both).</param>
    /// <param name="deviceType">FILE_DEVICE_* device type; defaults to <see cref="FileDeviceVendorDefault"/>.</param>
    public static uint VendorControlCode(uint function, uint access = FileAccessReadWrite, uint deviceType = FileDeviceVendorDefault)
    {
        if (function < FirstVendorFunction)
        {
            throw new ArgumentOutOfRangeException(nameof(function),
                $"Vendor function codes must be >= {FirstVendorFunction}; codes below that are reserved for Microsoft.");
        }
        return CtlCode(deviceType, function, MethodBuffered, access);
    }
}
