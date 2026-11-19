using WindowsDriver.Sdk;
using WindowsDriver.Sdk.Hid;

namespace HidGamepad.Driver;

/// <summary>Sample-specific IOCTLs for the gamepad.</summary>
public static class GamepadIoctlCodes
{
    /// <summary>In: a full 15-byte gamepad input report — replaces the device state.</summary>
    public static readonly uint SetInputState = WinIoctl.VendorControlCode(0x900, WinIoctl.FileAccessWrite);

    /// <summary>In: (u16 buttonMask, s16 x, s16 y, s16 z, s16 rx, s16 ry, s16 rz) — 14 bytes, merges into state.</summary>
    public static readonly uint UpdateState = WinIoctl.VendorControlCode(0x901, WinIoctl.FileAccessWrite);
}

/// <summary>
/// Sample driver: enumerates one virtual 6-axis / 12-button gamepad per devnode.
/// The HID report descriptor is built with the SDK's real item encoder, so the
/// bytes that ship to the Windows HID stack are spec-compliant.
/// </summary>
public sealed class SampleGamepadDriver : DriverBase
{
    public override Task<DeviceBase> OnDeviceAddAsync(DeviceAddContext addContext, DeviceContext deviceContext, CancellationToken cancellationToken)
    {
        deviceContext.Properties.Set(KnownDeviceProperties.FriendlyName, "Contoso Virtual Gamepad (SDK sample)");
        deviceContext.Properties.Set(KnownDeviceProperties.Manufacturer, "Contoso Devices");

        var device = new SampleGamepadDevice(deviceContext);
        deviceContext.CreateDeviceInterface(new Guid("9E573ADB-6D6A-4E37-8FB6-71C1A2E4D67B"));
        return Task.FromResult<DeviceBase>(device);
    }
}

/// <summary>
/// Virtual gamepad device: extends <see cref="HidDevice"/> with state-update
/// IOCTLs that simulate the physical controls (buttons, accelerometers, gyros).
/// </summary>
public sealed class SampleGamepadDevice : HidDevice
{
    private readonly object _gate = new();
    private GamepadState _state = new();

    public SampleGamepadDevice(DeviceContext context, HidDeviceOptions? options = null)
        : base(context, options ?? new HidDeviceOptions
        {
            ReportDescriptor = HidDescriptors.GamepadSixAxisTwelveButtons(),
        })
    {
        InputReportFactory = PackCurrent;
    }

    /// <summary>Current device state (thread-safe snapshot).</summary>
    public GamepadState State
    {
        get { lock (_gate) { return Snapshot(_state); } }
    }

    /// <summary>Merges axis/button changes into the current state.</summary>
    public void Update(Action<GamepadState> mutator)
    {
        lock (_gate)
        {
            mutator(_state);
        }
    }

    private byte[] PackCurrent()
    {
        lock (_gate)
        {
            return GamepadInputReport.Pack(_state);
        }
    }

    private static GamepadState Snapshot(GamepadState source)
    {
        var copy = new GamepadState { X = source.X, Y = source.Y, Z = source.Z, Rx = source.Rx, Ry = source.Ry, Rz = source.Rz };
        for (var b = 1; b <= GamepadState.ButtonCount; b++)
        {
            copy[b] = source[b];
        }
        return copy;
    }

    protected override Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        var code = request.IoControlCode;
        if (code == GamepadIoctlCodes.SetInputState)
        {
            if (request.InputBuffer.Length != GamepadInputReport.ReportSizeBytes)
            {
                request.Fail(NtStatus.InvalidParameter);
            }
            else
            {
                lock (_gate)
                {
                    _state = GamepadInputReport.Unpack(request.InputBuffer.Span);
                }
                Context.Log.Info("gamepad state replaced via IOCTL");
                request.Complete(NtStatus.Success, GamepadInputReport.ReportSizeBytes);
            }
        }
        else if (code == GamepadIoctlCodes.UpdateState)
        {
            var size = 2 + 6 * sizeof(short);
            if (request.InputBuffer.Length < size)
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                lock (_gate)
                {
                    var buttons = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(request.InputBuffer.Span);
                    for (var b = 1; b <= GamepadState.ButtonCount; b++)
                    {
                        _state[b] = (buttons & (1 << (b - 1))) != 0;
                    }
                    _state.X = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(request.InputBuffer.Span.Slice(2));
                    _state.Y = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(request.InputBuffer.Span.Slice(4));
                    _state.Z = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(request.InputBuffer.Span.Slice(6));
                    _state.Rx = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(request.InputBuffer.Span.Slice(8));
                    _state.Ry = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(request.InputBuffer.Span.Slice(10));
                    _state.Rz = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(request.InputBuffer.Span.Slice(12));
                }
                request.Complete(NtStatus.Success, size);
            }
        }
        else
        {
            return base.HandleDeviceControlAsync(request, ct);
        }
        return Task.CompletedTask;
    }
}
