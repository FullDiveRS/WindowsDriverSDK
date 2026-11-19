using WindowsDriver.Sdk;
using WindowsDriver.Sdk.Audio;

namespace VirtualAudio.Driver;

/// <summary>Sample-specific IOCTLs layered on top of the audio contract.</summary>
public static class SampleAudioIoctlCodes
{
    /// <summary>Out: f32 tone frequency (Hz).</summary>
    public static readonly uint GetToneFrequency = WinIoctl.VendorControlCode(0x900, WinIoctl.FileAccessRead);

    /// <summary>In: f32 tone frequency (Hz), 20..20000.</summary>
    public static readonly uint SetToneFrequency = WinIoctl.VendorControlCode(0x901, WinIoctl.FileAccessWrite);
}

/// <summary>
/// Sample driver: installs one virtual audio device per enumerated devnode.
/// The device is a <see cref="VirtualAudioDevice"/> with a sine source plus one
/// extra IOCTL pair (tone frequency) demonstrating how to extend the SDK contract.
/// </summary>
public sealed class SampleAudioDriver : DriverBase
{
    public override Task OnDriverEntryAsync(DriverContext context, CancellationToken cancellationToken)
    {
        context.Log.Info("sample audio driver entering (service '{0}')", context.DriverServiceName);
        context.DriverProperties.Set(KnownDeviceProperties.Manufacturer, "Contoso Devices");
        return Task.CompletedTask;
    }

    public override Task<DeviceBase> OnDeviceAddAsync(DeviceAddContext addContext, DeviceContext deviceContext, CancellationToken cancellationToken)
    {
        deviceContext.Log.Info("device add: {0} (hardware ids: {1})",
            addContext.DeviceInstanceId, string.Join(", ", addContext.HardwareIds));

        var device = new SampleAudioDevice(deviceContext, new AudioVirtualOptions
        {
            Mode = AudioVirtualMode.Capture,
            Source = new SineSource(frequencyHz: 440.0, amplitude: 0.25),
            // ~5 s of 48 kHz stereo 16-bit — enough to buffer a full second without drops.
            RingBufferBytes = 1_000_000,
            SupportedFormats =
            [
                new PcmFormat(44100, 16, 2),
                new PcmFormat(48000, 16, 2),
                new PcmFormat(96000, 16, 2),
            ],
        });

        // A device interface lets applications find and open the device.
        deviceContext.CreateDeviceInterface(new Guid("C4B9E4AF-E85A-4F03-B3B4-EB2E6BCF862B"));
        return Task.FromResult<DeviceBase>(device);
    }

    public override Task OnDriverUnloadAsync()
    {
        Context?.Log.Info("sample audio driver unloading");
        return Task.CompletedTask;
    }
}

/// <summary>Virtual audio device with a sample-specific tone-frequency IOCTL.</summary>
public sealed class SampleAudioDevice : VirtualAudioDevice
{
    private SineSource ToneSource { get; }

    public SampleAudioDevice(DeviceContext context, AudioVirtualOptions options)
        : base(context, options)
    {
        if (options.Source is not SineSource sine)
        {
            throw new ArgumentException("SampleAudioDevice requires a SineSource.", nameof(options));
        }
        ToneSource = sine;
        Context.Properties.Set(KnownDeviceProperties.FriendlyName, "Contoso Virtual Audio (SDK sample)");
    }

    /// <summary>Frequency the device's sine source is currently generating.</summary>
    public double ToneFrequency => ToneSource.FrequencyHz;

    protected override Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        var code = request.IoControlCode;
        if (code == SampleAudioIoctlCodes.GetToneFrequency)
        {
            if (request.OutputBuffer.Length < sizeof(float))
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                WireFormat.WriteFloat(request.OutputBuffer.Span, 0, (float)ToneSource.FrequencyHz);
                request.Complete(NtStatus.Success, sizeof(float));
            }
        }
        else if (code == SampleAudioIoctlCodes.SetToneFrequency)
        {
            if (request.InputBuffer.Length < sizeof(float))
            {
                request.Fail(NtStatus.BufferTooSmall);
            }
            else
            {
                var frequency = WireFormat.ReadFloat(request.InputBuffer.Span, 0);
                if (frequency is < 20f or > 20000f)
                {
                    request.Fail(NtStatus.InvalidParameter);
                }
                else
                {
                    ToneSource.FrequencyHz = frequency;
                    Context.Log.Info("tone frequency set to {0} Hz", frequency);
                    request.Complete(NtStatus.Success);
                }
            }
        }
        else
        {
            return base.HandleDeviceControlAsync(request, ct);
        }
        return Task.CompletedTask;
    }
}
