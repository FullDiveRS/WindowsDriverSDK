namespace WindowsDriver.Sdk;

/// <summary>
/// Thrown by driver callbacks to fail the surrounding request or lifecycle step
/// with a specific NTSTATUS. Uncaught generic exceptions complete with
/// <see cref="NtStatus.Unsuccessful"/>; an <see cref="InvalidRequestException"/>
/// completes with <see cref="NtStatus.InvalidDeviceRequest"/>.
/// </summary>
public class DriverException : Exception
{
    public NtStatus Status { get; }

    public DriverException(NtStatus status, string message, Exception? innerException = null)
        : base($"{status.ToHexString()}: {message}", innerException)
    {
        Status = status;
    }
}

/// <summary>Convenience for rejecting unsupported or malformed requests.</summary>
public sealed class InvalidRequestException : DriverException
{
    public InvalidRequestException(string message) : base(NtStatus.InvalidDeviceRequest, message) { }
}
