# Changelog

All notable changes to the Windows Driver SDK are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.0-preview.1] - 2026-11-18

First public preview of the SDK. Everything below ships in this bundle.

### Added

- `WindowsDriver.Sdk` core runtime:
  - Driver lifecycle model (`IDriver`: `OnDriverEntryAsync` / `OnDeviceAddAsync` / `OnDriverUnloadAsync`)
    and device lifecycle model (`DeviceBase`: prepare/release hardware, D0 entry/exit,
    query-remove, surprise-removal) mirroring the WDF callback order.
  - IO model: `IoQueue` with Sequential / Parallel / Manual dispatch modes (the WDF
    `WdfIoQueueDispatch*` equivalents), `IoRequest` with buffered completion,
    `IoResult` for request outcomes.
  - `NtStatus` enum with the common NTSTATUS values plus severity helpers.
  - `WinIoctl.CtlCode` implementing the exact `CTL_CODE` C macro bit layout, and the
    common `FILE_DEVICE_*` / method / access constants.
  - `DriverContext`, `DeviceContext`, `PropertyStore`, `IDriverRegistry` (+ in-memory
    implementation), `IDriverLog` (+ console / file / composite loggers).
  - `ByteRingBuffer`: thread-safe byte ring buffer used by audio and serial.
- Device-class modules:
  - **Audio**: `PcmFormat` + `AudioFormatNegotiator`, `AudioDeviceStateMachine`,
    `VolumeState` (dB/mute/ramp), `AudioIoctlCodes` contract, `VirtualAudioDevice`,
    `SineSource` generator and `PcmWavWriter` capture.
  - **HID**: `HidReportDescriptorBuilder` emitting real USB HID report descriptors
    (main/global/local items, report IDs, automatic bit padding), a descriptor parser
    for validation, the 9-byte HID class descriptor builder, `GamepadInputReport`
    pack/unpack, and a `HidDevice` wired for descriptor + input-report IOCTLs.
  - **Sensor**: `AccelerometerSimulator` (gravity + deterministic noise + impulses),
    `AmbientLightSimulator` (day-cycle lux curve), `SensorDevice` publishing readings
    over IOCTL and an event queue.
  - **Serial**: `SerialLineConfig` (baud/data bits/parity/stop bits/flow control),
    `VirtualSerialPort` loopback pair, `SerialDevice` with line-control IOCTLs and
    TX/RX statistics.
- `WindowsDriver.Interop`:
  - UMDF layer: C# contract mirrors of the key `IWDF*` interfaces (documented, without
    invented COM IIDs), the C-shim export contract, and `ManagedDriverBootstrap` —
    the entry point a native UMDF shim calls after hosting the .NET runtime via
    `hostfxr`.
  - KMDF layer: control-plane IOCTL contract (`KmdfIoctlCodes`), `KmdfControlPlane`
    (real `CreateFileW` / `DeviceIoControl` P/Invoke client for talking to a KMDF
    shim driver), and the request/event wire structs shared with the C shim.
  - `Win32Native` P/Invoke declarations (CreateFile, DeviceIoControl, CloseHandle).
- `WindowsDriver.Testing`: `TestDriverHost` / `TestDevice` (in-memory device bus that
  runs driver logic without Windows), `FakeTimeProvider` (advanceable .NET 8
  `TimeProvider`), `CapturedLog`, `FakeRegistry`, and check helpers.
- `WindowsDriver.InfGen`: `dotnet` tool that generates a spec-compliant driver INF
  from a JSON manifest (`infgen generate`) and lints an existing INF
  (`infgen validate`).
- Four sample drivers with unit tests and INF manifests: **VirtualAudio**,
  **HidGamepad**, **VirtualSensor**, **VirtualSerial**.
- `dotnet new` templates (`driver-umdf`, `driver-tests`, `driver-solution`).
- Install/uninstall/test-signing PowerShell scripts (`tools/`), a ready-to-build
  KMDF shim driver skeleton in C (`shim/WdsKmdfShim/`), a GitHub Actions CI workflow,
  and the full documentation set under `docs/`.
