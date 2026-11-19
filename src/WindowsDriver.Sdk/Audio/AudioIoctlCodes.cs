namespace WindowsDriver.Sdk.Audio;

/// <summary>
/// IOCTL contract implemented by <see cref="VirtualAudioDevice"/> (and any audio
/// driver built on the SDK's audio module). All IOCTLs use METHOD_BUFFERED.
/// Payload layouts are documented per field and implemented with
/// <see cref="WireFormat"/> — the same layouts the KMDF/UMDF shims forward.
/// </summary>
public static class AudioIoctlCodes
{
    private const uint DeviceType = WinIoctl.FileDeviceSound;

    // Vendor function codes start at WinIoctl.FirstVendorFunction (0x800).
    private static uint Ctl(uint function, uint access) => WinIoctl.VendorControlCode(function, access, DeviceType);

    /// <summary>Out: u32 count, then count × (u32 rate, u32 bits, u32 channels).</summary>
    public static readonly uint GetSupportedFormats = Ctl(0x800, WinIoctl.FileAccessRead);

    /// <summary>In: (u32 rate, u32 bits, u32 channels). Accepted only while Stopped; out-of-range rates are adjusted by the negotiator.</summary>
    public static readonly uint SetFormat = Ctl(0x801, WinIoctl.FileAccessWrite);

    /// <summary>Out: (u32 rate, u32 bits, u32 channels) — the active format.</summary>
    public static readonly uint GetFormat = Ctl(0x802, WinIoctl.FileAccessRead);

    /// <summary>No payload. Stopped/Paused → Running.</summary>
    public static readonly uint Start = Ctl(0x803, WinIoctl.FileAccessAny);

    /// <summary>No payload. Any → Stopped (position retained).</summary>
    public static readonly uint Stop = Ctl(0x804, WinIoctl.FileAccessAny);

    /// <summary>No payload. Running → Paused.</summary>
    public static readonly uint Pause = Ctl(0x805, WinIoctl.FileAccessAny);

    /// <summary>Out: (u64 bytePosition, u64 framePosition, u32 state).</summary>
    public static readonly uint GetPosition = Ctl(0x806, WinIoctl.FileAccessRead);

    /// <summary>In: (f32 masterGain 0..1, u8 mute, u32 rampMs, u32 channelCount, then channelCount × f32). channelCount 0 = master only.</summary>
    public static readonly uint SetVolume = Ctl(0x807, WinIoctl.FileAccessWrite);

    /// <summary>Out: (f32 masterGain target, f32 currentGain, u8 mute, u32 rampRemainingMs, u32 channelCount, channelCount × f32).</summary>
    public static readonly uint GetVolume = Ctl(0x808, WinIoctl.FileAccessRead);

    /// <summary>In: (u32 bytesToSkip). Skips playback/output bytes; used by clients that drop stale data.</summary>
    public static readonly uint SkipOutput = Ctl(0x809, WinIoctl.FileAccessWrite);
}
