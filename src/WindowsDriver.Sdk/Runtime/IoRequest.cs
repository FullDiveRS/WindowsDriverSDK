namespace WindowsDriver.Sdk;

/// <summary>The kind of IO operation a request represents (maps to WDF request types).</summary>
public enum IoRequestKind
{
    DeviceControl,
    Read,
    Write,
    Create,
    Close,
}

/// <summary>
/// A single IO request flowing from the host (test host, UMDF shim or KMDF
/// control plane) into a driver queue. The driver inspects the buffers and
/// finishes the request exactly once with <see cref="Complete"/> — the same
/// contract as <c>WdfRequestComplete</c>.
/// </summary>
public sealed class IoRequest
{
    private bool _completed;

    public IoRequest(IoRequestKind kind, uint ioControlCode, ReadOnlyMemory<byte> input, Memory<byte> output, ulong id = 0)
    {
        Kind = kind;
        IoControlCode = ioControlCode;
        InputBuffer = input;
        OutputBuffer = output;
        Id = id;
    }

    /// <summary>Host-assigned identifier, useful for tracing a request across layers.</summary>
    public ulong Id { get; }

    public IoRequestKind Kind { get; }

    /// <summary>The IOCTL code — meaningful only for <see cref="IoRequestKind.DeviceControl"/>.</summary>
    public uint IoControlCode { get; }

    /// <summary>SystemBuffer input side (IRP SystemBuffer with METHOD_BUFFERED).</summary>
    public ReadOnlyMemory<byte> InputBuffer { get; }

    /// <summary>Output side: the driver fills it and reports transferred bytes.</summary>
    public Memory<byte> OutputBuffer { get; }

    public bool IsCompleted => _completed;

    public NtStatus Status { get; private set; } = NtStatus.Pending;

    public int BytesTransferred { get; private set; }

    /// <summary>Raised exactly once when the request is completed (host-side observation).</summary>
    public event Action<IoRequest>? Completed;

    /// <summary>
    /// Completes the request. Completing twice throws — in WDF a double-complete
    /// bugchecks the machine, so treat this as a driver bug.
    /// </summary>
    public void Complete(NtStatus status, int bytesTransferred = 0)
    {
        if (_completed)
        {
            throw new InvalidOperationException($"Request {Id} was already completed with {Status.ToHexString()} — double completion is a driver bug.");
        }
        // Information semantics follow WdfRequestCompleteWithInformation: for
        // writes the count refers to bytes taken from the input buffer, for
        // everything else to bytes placed in the output buffer.
        var maxInformation = Kind == IoRequestKind.Write ? InputBuffer.Length : OutputBuffer.Length;
        if (bytesTransferred < 0 || bytesTransferred > maxInformation)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesTransferred),
                $"Transferred {bytesTransferred} bytes but the buffer only allows {maxInformation}.");
        }
        _completed = true;
        Status = status;
        BytesTransferred = bytesTransferred;
        Completed?.Invoke(this);
    }

    /// <summary>Completes with the given failure status and zero bytes.</summary>
    public void Fail(NtStatus status) => Complete(status, 0);
}

/// <summary>
/// An immutable snapshot of a completed request, as returned by the test host's
/// SendIoctl/Read/Write helpers.
/// </summary>
public sealed record IoResult(NtStatus Status, int BytesTransferred, byte[] Output)
{
    public bool IsSuccess => Status.IsSuccess();

    public static IoResult From(IoRequest request)
    {
        // For writes, report the transmitted bytes; for reads and IOCTLs, the output data.
        var source = request.Kind == IoRequestKind.Write ? request.InputBuffer.Span : request.OutputBuffer.Span;
        var count = Math.Min(request.BytesTransferred, source.Length);
        var output = new byte[count];
        source.Slice(0, count).CopyTo(output);
        return new IoResult(request.Status, request.BytesTransferred, output);
    }
}
