using System.Buffers.Binary;

namespace WindowsDriver.Sdk.Hid;

/// <summary>
/// SDK-level IOCTL contract for HID devices. These are the codes handled by
/// <see cref="HidDevice"/>; when a device is integrated with the Windows HID
/// stack through HidCx, the class extension maps this traffic onto the standard
/// hidclass IOCTLs (see docs/hid-guide.md).
/// </summary>
public static class HidIoctlCodes
{
    private static uint Ctl(uint function, uint access) => WinIoctl.VendorControlCode(function, access);

    /// <summary>Out: the raw report descriptor bytes.</summary>
    public static readonly uint GetReportDescriptor = Ctl(0x800, WinIoctl.FileAccessRead);

    /// <summary>Out: the 9-byte HID class descriptor.</summary>
    public static readonly uint GetDeviceDescriptor = Ctl(0x801, WinIoctl.FileAccessRead);

    /// <summary>Out: u16 wire size of the largest input report (ID byte included).</summary>
    public static readonly uint GetInputReportSize = Ctl(0x802, WinIoctl.FileAccessRead);

    /// <summary>Out: the current input report (ID byte first).</summary>
    public static readonly uint GetInputReport = Ctl(0x803, WinIoctl.FileAccessRead);

    /// <summary>In: feature report bytes; the device stores and echoes them.</summary>
    public static readonly uint SetFeatureReport = Ctl(0x804, WinIoctl.FileAccessWrite);

    /// <summary>Out: the last feature report set via <see cref="SetFeatureReport"/>.</summary>
    public static readonly uint GetFeatureReport = Ctl(0x805, WinIoctl.FileAccessRead);
}

/// <summary>Configuration for <see cref="HidDevice"/>.</summary>
public sealed class HidDeviceOptions
{
    /// <summary>Raw report descriptor. Defaults to the canonical 6-axis/12-button gamepad.</summary>
    public byte[]? ReportDescriptor { get; init; }

    /// <summary>Produces the current packed input report (report-ID byte first). Defaults to a neutral gamepad state.</summary>
    public Func<byte[]>? InputReportFactory { get; init; }

    public ushort BcdHid { get; init; } = 0x0111;
    public byte CountryCode { get; init; }

    public static HidDeviceOptions Default => new();
}

/// <summary>
/// A complete virtual HID device: serves the report and class descriptors over
/// IOCTL and delivers the current input report on Read requests. The input
/// report content is produced by <see cref="HidDeviceOptions.InputReportFactory"/>,
/// so the owning driver decides how state (buttons, axes) becomes wire data.
/// </summary>
public class HidDevice : DeviceBase
{
    private readonly HidDeviceOptions _options;
    private readonly byte[] _reportDescriptor;
    private readonly byte[] _classDescriptor;
    private byte[] _featureReport = [];

    public HidDevice(DeviceContext context, HidDeviceOptions? options = null)
        : base(context)
    {
        _options = options ?? HidDeviceOptions.Default;
        _reportDescriptor = _options.ReportDescriptor ?? HidDescriptors.GamepadSixAxisTwelveButtons();
        _classDescriptor = HidDescriptor.Build(_reportDescriptor, _options.BcdHid, _options.CountryCode);
        InputReportFactory = _options.InputReportFactory ?? (() => GamepadInputReport.Pack(new GamepadState()));

        DefaultQueue
            .OnDeviceControl(HandleDeviceControlAsync)
            .OnRead(HandleReadAsync);
    }

    public Func<byte[]> InputReportFactory { get; set; }

    /// <summary>Last feature report written by a client (SetFeatureReport).</summary>
    public byte[] FeatureReport => _featureReport;

    public int InputReportSize
    {
        get
        {
            var factory = InputReportFactory();
            return factory.Length;
        }
    }

    public override Task OnPrepareHardwareAsync(CancellationToken cancellationToken)
    {
        Context.Log.Info("HID device ready: {0}-byte report descriptor", _reportDescriptor.Length);
        return Task.CompletedTask;
    }

    private Task HandleReadAsync(IoRequest request, CancellationToken ct)
    {
        var report = InputReportFactory();
        if (request.OutputBuffer.Length < report.Length)
        {
            request.Fail(NtStatus.BufferTooSmall);
            return Task.CompletedTask;
        }
        report.CopyTo(request.OutputBuffer);
        request.Complete(NtStatus.Success, report.Length);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the <see cref="HidIoctlCodes"/> contract. Subclasses override
    /// this to add device-specific IOCTLs and fall back to base for the rest.
    /// </summary>
    protected virtual Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        var code = request.IoControlCode;
        if (code == HidIoctlCodes.GetReportDescriptor)
        {
            CopyTo(request, _reportDescriptor);
        }
        else if (code == HidIoctlCodes.GetDeviceDescriptor)
        {
            CopyTo(request, _classDescriptor);
        }
        else if (code == HidIoctlCodes.GetInputReportSize)
        {
            if (request.OutputBuffer.Length < sizeof(ushort))
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(request.OutputBuffer.Span, (ushort)InputReportSize);
                request.Complete(NtStatus.Success, sizeof(ushort));
            }
        }
        else if (code == HidIoctlCodes.GetInputReport)
        {
            var report = InputReportFactory();
            CopyTo(request, report);
        }
        else if (code == HidIoctlCodes.SetFeatureReport)
        {
            _featureReport = request.InputBuffer.ToArray();
            request.Complete(NtStatus.Success, _featureReport.Length);
        }
        else if (code == HidIoctlCodes.GetFeatureReport)
        {
            CopyTo(request, _featureReport);
        }
        else
        {
            request.Fail(NtStatus.InvalidDeviceRequest);
        }
        return Task.CompletedTask;
    }

    private static void CopyTo(IoRequest request, byte[] payload)
    {
        if (request.OutputBuffer.Length < payload.Length)
        {
            request.Fail(NtStatus.BufferTooSmall);
            return;
        }
        payload.CopyTo(request.OutputBuffer);
        request.Complete(NtStatus.Success, payload.Length);
    }
}
