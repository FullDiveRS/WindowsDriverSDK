# VirtualAudio sample

A complete virtual audio device in C#: PCM format negotiation, a ring buffer
standing in for the hardware DMA engine, a start/pause/stop state machine,
volume with ramps and mute, and the `AudioIoctlCodes` IOCTL contract — plus one
sample-specific IOCTL pair (tone frequency) showing how to extend the contract.

## What it demonstrates

- **Driver entry** — `SampleAudioDriver : DriverBase` creates one
  `SampleAudioDevice : VirtualAudioDevice` per enumerated devnode and registers
  a device interface.
- **Format negotiation** — `AudioFormatNegotiator` picks the closest supported
  format for a client request (48 kHz requested while running 44.1 kHz? →
  44.1 kHz).
- **Time-driven audio** — the device's byte position advances with the host
  `TimeProvider` while Running, exactly like a real render/capture clock. The
  `SineSource` fills the ring buffer on demand; the tests advance the fake
  clock and read deterministic PCM.
- **Volume** — master gain, per-channel gains, mute, and linear ramps; gain is
  applied to 16-bit samples on the output path.
- **Extensibility** — `SampleAudioDevice` overrides
  `HandleDeviceControlAsync`, handles its own IOCTLs and delegates the rest to
  the base contract.

## Layout

```
Driver/SampleAudioDriver.cs   driver + device (extends VirtualAudioDevice)
manifest.json                 InfGen manifest → INF (Media class, UMDF)
Tests/AudioSampleTests.cs     9 tests: negotiation, position, volume, IOCTLs, INF
```

## Try it

```bash
dotnet test samples/VirtualAudio/Tests          # runs everywhere, no Windows needed
infgen generate --manifest samples/VirtualAudio/manifest.json
```

## Going further

- Read `docs/audio-guide.md` for the Windows audio stack mapping (PortCls /
  AVStream / AudioGraph loopback) and what changes in production.
- Swap `SineSource` for a custom `IAudioSource` (microphone emulation, network
  stream) — the device, contract and tests stay the same.
