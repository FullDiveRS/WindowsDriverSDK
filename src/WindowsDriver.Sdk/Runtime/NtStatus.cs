using System.Text;

namespace WindowsDriver.Sdk;

/// <summary>
/// NTSTATUS values used by the Windows kernel and returned from IOCTLs.
/// Only the values a driver author regularly needs are listed here; extend as
/// required. The numeric values match winnt.h / ntstatus.h exactly.
/// </summary>
public enum NtStatus : uint
{
    Success = 0x00000000,
    Timeout = 0x00000102,          // informational
    Pending = 0x00000103,          // informational
    BufferOverflow = 0x80000005,   // warning
    Unsuccessful = 0xC0000001,
    NoSuchDevice = 0xC000000E,
    InvalidParameter = 0xC000000D,
    EndOfFile = 0xC0000011,
    InvalidDeviceRequest = 0xC0000010,
    AccessDenied = 0xC0000022,
    BufferTooSmall = 0xC0000023,
    ObjectNameNotFound = 0xC0000034,
    Cancelled = 0xC0000120,
    DeviceNotReady = 0xC00000A3,
    IoTimeout = 0xC00000B5,
    NotSupported = 0xC00000BB,
    InvalidDeviceState = 0xC0000184,
}

/// <summary>Severity and convenience checks for <see cref="NtStatus"/>.</summary>
public static class NtStatusExtensions
{
    /// <summary>NT_SUCCESS: matches the C macro ((NTSTATUS)s &gt;= 0) — success and informational severities.</summary>
    public static bool IsSuccess(this NtStatus status) => (uint)status < 0x80000000u;

    /// <summary>NT_INFORMATION: severity 1.</summary>
    public static bool IsInformational(this NtStatus status) => ((uint)status >> 30) == 1;

    /// <summary>NT_WARNING: severity 2 (includes <see cref="NtStatus.BufferOverflow"/>).</summary>
    public static bool IsWarning(this NtStatus status) => ((uint)status >> 30) == 2;

    /// <summary>NT_ERROR: severity 3.</summary>
    public static bool IsError(this NtStatus status) => ((uint)status >> 30) == 3;

    /// <summary>The two-bit NTSTATUS severity field (bits 30–31).</summary>
    public static uint Severity(this NtStatus status) => (uint)status >> 30;

    /// <summary>"0xC0000010 (InvalidDeviceRequest)" style rendering for logs.</summary>
    public static string ToHexString(this NtStatus status)
    {
        var sb = new StringBuilder(20);
        sb.Append("0x").Append(((uint)status).ToString("X8"));
        if (((uint)status & 0xC0000000u) != 0 || status == NtStatus.Success)
        {
            sb.Append(" (").Append(status.ToString()).Append(')');
        }
        return sb.ToString();
    }
}
