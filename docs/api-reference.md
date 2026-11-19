# API reference

A type-by-type map of the SDK surface. Namespaces: `WindowsDriver.Sdk` (core),
`WindowsDriver.Sdk.Audio/Hid/Sensor/Serial` (modules),
`WindowsDriver.Interop.Umdf/Kmdf/Win32`, `WindowsDriver.Testing`,
`WindowsDriver.InfGen` (tool).

## Core runtime (`WindowsDriver.Sdk`)

### Lifecycle

| Type | Member | Notes |
| --- | --- | --- |
| `IDriver` | `OnDriverEntryAsync(DriverContext, ct)` | first callback |
| | `OnDeviceAddAsync(DeviceAddContext, DeviceContext, ct) → DeviceBase` | per device |
| | `OnDriverUnloadAsync()` | last callback |
| `DriverBase` | abstract base, `CreateDevice<T>(deviceContext)` helper | no-op entry/unload |
| `DriverContext` | `Log`, `Registry`, `TimeProvider`, `DriverServiceName`, `DriverProperties` | created by the host |
| `DeviceBase` | `Context`, `DefaultQueue`, `CreateQueue(options)`, `IsD0`, `IsHardwarePrepared` | override `OnPrepareHardwareAsync`, `OnD0EntryAsync/OnD0ExitAsync`, `OnReleaseHardwareAsync`, `OnQueryRemoveAsync`, `OnSurpriseRemovalAsync` |
| `DriverHost` | `StartAsync<TDriver>()`, `AddDeviceAsync(addContext)`, `RemoveDeviceAsync`, `SurpriseRemoveDeviceAsync`, `ShutdownAsync` | the WDF order, with rollback |
| `DeviceAddContext` | `DeviceInstanceId`, `HardwareIds`, `CompatibleIds`, `Properties`, `IsRawDevice` | bus-reported identity |
| `DeviceContext` | `DeviceInstanceId`, `HardwareIds`, `Properties`, `Log`, `CreateDeviceInterface(guid, ref?)` | per device, host-created |
| `DevicePowerState` | `Unspecified, D0..D3` | WDM values |

### IO

| Type | Member | Notes |
| --- | --- | --- |
| `IoRequest` | `Kind`, `IoControlCode`, `InputBuffer`, `OutputBuffer`, `Complete(status, bytes)`, `Fail(status)` | complete exactly once; write counts = input bytes |
| `IoResult` | `Status`, `BytesTransferred`, `Output`, `IsSuccess` | snapshot from the test host |
| `IoQueue` | `OnDeviceControl/OnRead/OnWrite(handler)`, `Submit`, `ManualRequests`, `DrainAsync`, `StopAsync` | dispatch via options |
| `IoQueueOptions` | `Dispatch` (Sequential/Parallel/Manual), `Name` | |
| `QueueHandlers` | `DeviceControl`, `Read`, `Write` — `Func<IoRequest, ct, Task>` | unregistered → `InvalidDeviceRequest` |

### Status and encoding

| Type | Notes |
| --- | --- |
| `NtStatus` | Success, Pending, Timeout, BufferOverflow, Unsuccessful, NoSuchDevice, InvalidParameter, EndOfFile, InvalidDeviceRequest, AccessDenied, BufferTooSmall, ObjectNameNotFound, Cancelled, DeviceNotReady, IoTimeout, NotSupported, InvalidDeviceState |
| `NtStatusExtensions` | `IsSuccess` (NT_SUCCESS semantics: < 0x80000000), `IsInformational`, `IsWarning`, `IsError`, `Severity`, `ToHexString` |
| `WinIoctl` | `CtlCode` (exact C-macro layout), `VendorControlCode` (enforces ≥ 0x800), `DeviceType/Function/Method/AccessFromControl`, `FileDevice*` constants, `Method*`, `FileAccess*` |
| `WireFormat` | little-endian `Read/Write` for i16/u16/i32/u32/i64/u64/f32/f64 with `BufferTooSmall` bounds checks |
| `DriverException` | carries `Status`; `InvalidRequestException` = InvalidDeviceRequest |

### Environment

| Type | Notes |
| --- | --- |
| `IDriverLog` | `IsEnabled(level)`, `Log(level, message)`; extensions `Trace/Info/Warning/Error(fmt, args)` |
| implementations | `ConsoleDriverLog`, `FileDriverLog`, `CompositeDriverLog`, `ScopedDriverLog` (prefix), `NullDriverLog` |
| `IDriverRegistry` | `ReadString/ReadDword/WriteString/WriteDword/DeleteValue` — the service `Parameters` key |
| `MemoryRegistry` | in-memory implementation (extendable) |
| `PropertyStore` | typed string-keyed store (`GetString/GetDword/GetBool/GetGuid/Set/Remove`) |
| `KnownDeviceProperties` | FriendlyName, Manufacturer, DeviceDescription, DriverVersion, InterfaceClassGuid |
| `ByteRingBuffer` | `Write`, `Read`, `Skip`, `Clear`, `Count`, `FreeSpace`, `DroppedBytes`, `TotalBytesWritten/Read` |

## Audio (`WindowsDriver.Sdk.Audio`)

| Type | Notes |
| --- | --- |
| `PcmFormat` | record (rate, bits, channels); `BytesPerFrame`, `BytesPerSecond` |
| `AudioFormatNegotiator` | `Negotiate(requested) → AudioNegotiationResult` (accepted format or InvalidParameter) |
| `AudioDeviceStateMachine` | `Start/Pause/Stop`, `Snapshot`, `Transitioned` event; invalid → InvalidDeviceState |
| `VolumeState` | `SetMasterGain(target, ramp)`, `SetMute`, `SetChannelGain`, `CurrentGain`, `CurrentGainDecibels`, `RampRemaining` |
| `AudioIoctlCodes` | GetSupportedFormats, SetFormat, GetFormat, Start, Stop, Pause, GetPosition, SetVolume, GetVolume, SkipOutput |
| `VirtualAudioDevice` | composes all of it; options: `Mode` (Capture/Render/Loopback), `Source`, `Sink`, `RingBufferBytes`, `SupportedFormats`, `ApplyVolumeToOutput` |
| `SineSource` | frequency/amplitude generator (mutable frequency) |
| `IAudioSource` / `IAudioSink` | `FillFrames(span, format)` / `WriteFrames(span, format)` |
| `PcmVolume` | `ApplyMasterGain16(span, gain)` — 16-bit in-place scaling |
| `PcmWavWriter` | `IAudioSink` writing a RIFF/WAVE stream; `WriteTrailer()` patches sizes |

## HID (`WindowsDriver.Sdk.Hid`)

| Type | Notes |
| --- | --- |
| `HidReportDescriptorBuilder` | fluent items (UsagePage, Usage, Logical*, ReportId/Size/Count, Input/Output/Feature, Collection), conveniences `Buttons`, `Axis`, `PaddingInput`, `ToBytes` |
| `HidReportDescriptorParser` | `Parse(bytes) → HidDescriptorSummary` (reports, fields, depth, UsesReportIds) |
| `HidDescriptor` | `Build(reportDescriptor, bcdHid, country)` — 9-byte class descriptor |
| `HidDescriptors` | `GamepadSixAxisTwelveButtons()` — canonical descriptor |
| `GamepadState` | buttons 1..12 (indexer), X/Y/Z/Rx/Ry/Rz int16 |
| `GamepadInputReport` | `Pack/Unpack`, `ReportId = 1`, `ReportSizeBytes = 15` |
| `HidDevice` | IOCTL contract (`HidIoctlCodes`), `InputReportFactory`, feature echo |
| enums | `HidUsagePage`, `GenericDesktopUsage`, `HidCollectionType`, `HidMainItemFlags` |

## Sensor (`WindowsDriver.Sdk.Sensor`)

| Type | Notes |
| --- | --- |
| `ISensorSource` | `Kind`, `Unit`, `ValueCount`, `SampleRateHz`, `Sample(timestamp)` |
| `AccelerometerSimulator` | gravity + seeded noise + `AddImpulse(at, duration, accel)` |
| `AmbientLightSimulator` | day curve + flicker + `OverrideLux` |
| `SensorReading` | (kind, timestamp, values, unit) |
| `SensorDevice` | `SampleOnce`, `LatestReading(i)`, `Readings` channel, background loop on D0 |
| `SensorIoctlCodes` | GetSensorCount, GetSensorInfo, GetLatestReading, SetSampleRate |

## Serial (`WindowsDriver.Sdk.Serial`)

| Type | Notes |
| --- | --- |
| `SerialLineConfig` | record + `Validate()` (UART rules) + `ReadFrom/WriteTo` (8-byte wire) |
| `SerialDevice` | queues (`RxQueue/TxQueue`), `ApplyConfig`, `InjectReceive`, `Transmit`, `AssertBreak`, `InjectFramingError`, `Cts/Dsr/BreakActive` |
| `SerialLoopbackPair` | cross-connects two non-loopback ports |
| `SerialIoctlCodes` | GetLineConfig, SetLineConfig, Transmit, Purge, GetStatus, GetRxCount |
| enums | `SerialParity`, `SerialStopBits`, `SerialFlowControl` |

## Interop (`WindowsDriver.Interop.*`)

| Type | Notes |
| --- | --- |
| `Umdf.ShimHeader` (`UmdfShimContract.ShimHeader`) | the C ABI header (single source of truth) |
| `Umdf.ManagedDriverBootstrap` | `WdsInitialize/WdsDeviceAdd/WdsQueueRequest/WdsDeviceD0Entry/WdsDeviceD0Exit/WdsDeviceRelease/WdsDriverUnload` — `[UnmanagedCallersOnly]` |
| `Umdf.UmdfShimSettings` | function pointers + names (5 pointers, `WireSize = 5 × IntPtr.Size`) |
| `Umdf.IWdf*` interfaces | documentation contracts mirroring wudfddi.h callbacks (no fake COM IIDs) |
| `Kmdf.KmdfShimProtocol` | Ping/GetVersion/SendCommand/ReadEvents/AcknowledgeEvents + `Magic` ("WSHI") |
| `Kmdf.KmdfCommandHeader/KmdfResultHeader/KmdfEventRecord` | packed wire structs (8/8/16 bytes) |
| `Kmdf.KmdfControlPlane` | `Open`, `Ping`, `GetVersion`, `SendCommand`, `ReadEvents` over real Win32 IOCTLs |
| `Win32.Win32Native` | `CreateFileW`, `DeviceIoControl`, `SendIoctl` helper |

## Testing (`WindowsDriver.Testing`)

| Type | Notes |
| --- | --- |
| `TestDriverHost` | wraps `DriverHost` + captured log + fake registry + fake clock; `StartAsync`, `AddDeviceAsync`, `ShutdownAsync` |
| `TestDriverHostBuilder` | `UseFakeTime(start?)`, `UseRealTime()`, `WithRegistry`, `WithLogMinimum` |
| `TestDevice` | `SendIoctlAsync(code, input?, outputSize?)`, `ReadAsync`, `WriteAsync` (5 s timeout default), exposes `Device`/`Context`/`DefaultQueue` |
| `FakeTimeProvider` | `Advance(delta)`, `AdvanceTo(when)`; timers fire synchronously, no backwards time |
| `CapturedLog` | `Messages`, `Entries`, `Contains`, `ContainsError`, `Clear` |
| `FakeRegistry` | seedable in-memory registry |
| `Check` | `Status`, `Success`, `Bytes` — framework-agnostic asserts |
| `TestSync` | `WaitUntilAsync(condition, timeout)` polling helper |

## InfGen (`WindowsDriver.InfGen`)

| Type | Notes |
| --- | --- |
| `Program` (`infgen`) | `generate --manifest x.json [--out] [--arch]`, `validate --inf f.inf`, `schema` |
| `DriverManifest` | JSON manifest record + `Validate`; camelCase + comments tolerated |
| `InfGenerator` | `Generate(manifest, archs?)`; `NormalizeGuid`; `SdkVersion` |
| `InfValidator` | `Validate(text) → InfIssue[]`; required sections, GUIDs, DriverVer, duplicates, model refs, string keys |
| `DriverFile` / `DriverModel` | manifest sub-records |

## Versioning

`Directory.Build.props` holds the single `<Version>` (currently
`0.1.0-preview.1`) applied to all packages; `global.json` pins the .NET SDK
(8.0.x, latestFeature).
