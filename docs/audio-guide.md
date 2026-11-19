# Audio guide

The audio module (`WindowsDriver.Sdk.Audio`) gives you everything a virtual
audio device needs minus the OS glue: PCM format negotiation, a hardware-like
ring buffer, a start/pause/stop state machine, volume with ramps, a position
clock, and the IOCTL contract. `VirtualAudioDevice` composes all of it; the
VirtualAudio sample adds one custom IOCTL pair on top.

## Core types

| Type | Role |
| --- | --- |
| `PcmFormat(SampleRateHz, BitsPerSample, Channels)` | interleaved little-endian PCM format |
| `AudioFormatNegotiator` | picks the closest supported format for a request |
| `AudioDeviceStateMachine` | Stopped/Paused/Running with WDF-style transitions |
| `VolumeState` | master + per-channel gains, mute, linear ramps, dB conversion |
| `ByteRingBuffer` | thread-safe FIFO with drop counting (the "DMA buffer") |
| `SineSource` / `IAudioSource` | produces frames while running |
| `IAudioSink` / `PcmWavWriter` | consumes frames (WAV capture for tests/demos) |
| `VirtualAudioDevice : DeviceBase` | the composed device |

## The IOCTL contract

`AudioIoctlCodes` defines ten vendor IOCTLs on `FILE_DEVICE_SOUND`
(functions ≥ 0x800, all METHOD_BUFFERED). The documented payload layouts:

- **GetSupportedFormats** — out: u32 count, then count × (u32 rate, u32 bits, u32 channels).
- **SetFormat** — in: the same triple. Accepted only while Stopped
  (`STATUS_INVALID_DEVICE_STATE` otherwise). If the request is not exactly
  supported, the negotiator picks the closest format and *succeeds* with the
  accepted format in the output — the caller reads it back rather than
  guessing. (Returning a warning status here would make the Win32 call fail;
  warnings are not success from `DeviceIoControl`'s perspective.)
- **Start / Pause / Stop** — state machine transitions; invalid transitions
  fail with `STATUS_INVALID_DEVICE_STATE`, transitions are idempotent.
- **GetPosition** — out: (u64 bytePosition, u64 framePosition, u32 state).
- **SetVolume** — in: (f32 master 0..1, u8 mute, u32 rampMs, u32 channelCount,
  channelCount × f32). Channel 0 means master-only.
- **GetVolume** — out: target, current (post-ramp), mute, remaining ramp ms,
  channel count, per-channel gains.
- **SkipOutput** — in: u32 bytes to drop from the ring (clients that fall
  behind use this instead of reading stale audio).

## How the clock works

A real audio device advances its position with hardware time. The SDK's
`VirtualAudioDevice` advances *logically*: while `Running`, the number of
frames owed equals `(now − startTime − pausedTime) × sampleRate`, computed
from the host's `TimeProvider` and lazily materialized into the ring buffer on
the next Read/Write/GetPosition call. Consequences:

- Time is exact — advance the fake clock 1 s in a test and `GetPosition`
  reports exactly `sampleRate` frames.
- No background thread needed in tests; in production the UMDF/KMDF host can
  drive the pump from a real timer or interrupt-equivalent.
- Overrun drops are counted (`ByteRingBuffer.DroppedBytes`) rather than
  silently corrupting the stream, mirroring real DMA underrun/overrun
  behavior.

## Writing your own device

```csharp
public sealed class MyAudioDevice : VirtualAudioDevice
{
    public MyAudioDevice(DeviceContext context)
        : base(context, new AudioVirtualOptions
        {
            Mode = AudioVirtualMode.Capture,          // or Render / Loopback
            Source = new SineSource(1000.0, 0.3),
            RingBufferBytes = 1_000_000,              // ~5 s of 48 kHz stereo
            SupportedFormats = [ new(48000, 16, 2), new(48000, 24, 2) ],
        })
    {
    }

    protected override Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        // handle your IOCTLs, then:
        return base.HandleDeviceControlAsync(request, ct);
    }
}
```

Modes:

- **Capture** — the `IAudioSource` fills the ring; clients `Read` frames
  (microphone-style).
- **Render** — clients `Write` frames; the `IAudioSink` consumes them
  (speaker-style; `PcmWavWriter` is a convenient sink).
- **Loopback** — writes come back on the next read (test harness).

## Applying volume

`PcmVolume.ApplyMasterGain16` scales interleaved 16-bit samples in place with
clamping; `VirtualAudioDevice` applies the current gain (ramp included, mute
forces zero) on the device's output path when `AudioVirtualOptions.ApplyVolumeToOutput`
is true (the default). The volume test in the sample asserts the halved peak
and the silent mute case numerically.

## Integration with the Windows audio stack

Where does this plug in on a real system?

| Path | What it is | Use when |
| --- | --- | --- |
| UMDF driver + your client | the SDK contract as-is; your client app opens the device interface and exchanges PCM | custom audio pipelines, virtual devices for your own app |
| UMDF HID-style mini-driver | n/a for audio | — |
| KMDF/AVStream miniport | native; the kernel side implements a miniport (PortCls) and can call the shim protocol for policy | real audio endpoints other apps must see (WaveOut/WASAPI) |
| Audio Processing Object (APO) | user-mode COM in the audio engine; conceptually C#-hostable through the same COM-shim approach | DSP effects in the system pipeline |

The most practical first step is a virtual endpoint paired with a client
service; moving to system-visible endpoints means the AVStream/PortCls
miniport in C with the SDK's control plane handling everything above the
DDI boundary.
