# UMDF guide — hosting C# drivers in WUDFHost

UMDF (User-Mode Driver Framework) is the right first target for a C# driver:
it runs in a user-mode host process (`WUDFHost.exe`), a driver bug produces an
error instead of a blue screen, and most device classes (HID via HidCx,
sensors via SensorCx, custom buses) have user-mode paths.

## What Microsoft supports vs. what this SDK adds

As of the WDK available in November 2026, Microsoft's supported languages for
UMDF driver code are C++ and C++/COM. There is no official C# story. This SDK
adds one with a **managed-host architecture**: a small C++/COM shim gets loaded
by `WUDFHost`, hosts the .NET runtime via `hostfxr`, and forwards every UMDF
callback to your C# driver through a frozen ABI.

You write only the C# part. The shim is mechanical forwarding code — this guide
is its implementation spec, and the managed side of the ABI is already shipped.

## The two sides of the ABI

### Managed side (shipped: `WindowsDriver.Interop.Umdf`)

- `ManagedDriverBootstrap` — seven `[UnmanagedCallersOnly]` entry points the
  shim resolves with `hostfxr`'s `load_assembly_and_get_function_pointer`:

  | Entry point | Called when | Bridges to |
  | --- | --- | --- |
  | `WdsInitialize` | `IDriverEntry::OnInitialize` | `DriverHost.StartAsync(driverType)` |
  | `WdsDeviceAdd` | `EvtDriverDeviceAdd` | `DriverHost.AddDeviceAsync` |
  | `WdsQueueRequest` | every request in a queue | `IoQueue.Submit(IoRequest)` |
  | `WdsDeviceD0Entry` / `WdsDeviceD0Exit` | power transitions | `DeviceBase.D0EntryAsync` / `D0ExitAsync` |
  | `WdsDeviceRelease` | `EvtDeviceReleaseHardware` | `DeviceBase.ReleaseHardwareAsync` |
  | `WdsDriverUnload` | `IDriverEntry::OnDeinitialize` | `DriverHost.ShutdownAsync` |

- `UmdfShimSettings` — the struct the shim passes to `WdsInitialize`: function
  pointers for `CompleteRequest` (managed → shim), `Log` (managed → shim, UTF-8
  lines) and `ArmQueue`, plus the UTF-8 service name and the fully-qualified
  managed driver type name (e.g. `VirtualAudio.Driver.SampleAudioDriver, VirtualAudio.Driver`).
- `UmdfShimContract.ShimHeader` — the C header text, the single source of truth
  for both sides.

### Native side (your shim project)

Implement in C++/COM inside a UMDF driver DLL (call it `WdsShimU.dll`):

1. Standard COM scaffolding (`DllGetClassObject` + class factory) so
   `WUDFHost` can create the driver object from the CLSID your INF registers
   (see the `[<install>.Wdf]` `UmdfService` wiring that `infgen` emits).
2. `IDriverEntry::OnInitialize` → prepare `WDS_SHIM_SETTINGS`, locate
   `hostfxr.dll`, initialize with your assembly's `.runtimeconfig.json`, get
   function pointers for the seven `Wds*` entry points, call `WdsInitialize`.
3. `OnDeviceAdd` → read the hardware IDs from the device property store and
   call `WdsDeviceAdd(instanceId, hardwareIds[], count)`.
4. Queue plumbing — create an `IWDFIoQueue` per managed `IoQueue`; on each
   request: `WdfRequestRetrieveInputBuffer`/`OutputBuffer` → `WdsQueueRequest`
   with `(requestId, kind, ioctl, input, inputLen, output, outputLen)`. When
   the managed side later calls your `CompleteRequest(requestId, status,
   bytes)` callback, finish with `WdfRequestCompleteWithInformation` — for
   write requests the information is input bytes, matching the SDK contract.
5. Power/PnP callbacks → `WdsDeviceD0Entry(previousState)` /
   `WdsDeviceD0Exit(newState)` / `WdsDeviceRelease()`.
6. `Log` callback → forward UTF-8 lines to WPP/ETW tracing.

## Lifecycle mapping

| UMDF/WDF callback | SDK callback | Notes |
| --- | --- | --- |
| `IDriverEntry::OnInitialize` | `IDriver.OnDriverEntryAsync(DriverContext)` | read `Parameters` key via `DriverContext.Registry` |
| `EvtDriverDeviceAdd` | `IDriver.OnDeviceAddAsync(addContext, deviceContext)` | return your `DeviceBase`; host runs PrepareHardware + D0Entry |
| `EvtDevicePrepareHardware` | `DeviceBase.OnPrepareHardwareAsync` | map resources |
| `EvtDeviceD0Entry` | `DeviceBase.OnD0EntryAsync(previousState)` | start streams/timers |
| `EvtDeviceD0Exit` | `DeviceBase.OnD0ExitAsync(newState)` | stop streams |
| `EvtDeviceReleaseHardware` | `DeviceBase.OnReleaseHardwareAsync` | free resources |
| `EvtDeviceQueryRemove` | `DeviceBase.OnQueryRemoveAsync` | return normally to approve; throw `DriverException` to deny |
| `EvtDeviceSurpriseRemoval` | `DeviceBase.OnSurpriseRemovalAsync` | unplug — stop touching hardware |
| `IDriverEntry::OnDeinitialize` | `IDriver.OnDriverUnloadAsync` | last callback |

`IoQueue` dispatch modes map 1:1 to `WdfIoQueueDispatch*`:
Sequential / Parallel / Manual.

## Buffers and copying

All SDK contracts use `METHOD_BUFFERED` IOCTLs, so the shim can simply copy
`SystemBuffer` in and out — no MDL plumbing. The managed side validates every
access with `WireFormat` (throws `STATUS_BUFFER_TOO_SMALL`, which the shim
maps straight back to the request).

## Practical notes

- **Threading**: UMDF delivers callbacks on host threads; the SDK queues
  serialize or parallelize exactly like WDF. Handlers may run concurrently in
  Parallel mode — guard device state (the samples use small `lock` blocks or
  concurrent structures).
- **Startup cost**: hosting the CLR adds tens of milliseconds to driver load.
  Irrelevant for most drivers; if your device must respond within microseconds
  of arrival, pre-warm in `OnDriverEntryAsync`.
- **Crash semantics**: an unhandled managed exception completes the request
  with `STATUS_UNSUCCESSFUL`; it cannot take down the machine. `WUDFHost`
  restarts the driver host on repeated failure, same as native UMDF drivers.
- **Testing**: `TestDriverHost` runs the identical lifecycle — CI on Linux
  gates the same code that ships in `WUDFHost`.

## Checklist to go from repo to deployed UMDF driver

1. `dotnet new driver-umdf` → write the driver + tests (any OS).
2. `infgen generate --manifest <name>.manifest.json` → INF (UMDF wiring
   included: `UmdfService`, `UmdfLibraryVersion`, `ServiceBinary`).
3. Build the C++/COM shim per the checklist above; register it as the
   `UmdfService` binary in the INF.
4. Sign the package (test cert locally, attestation for distribution) — see
   [inf-and-signing.md](inf-and-signing.md).
5. `tools/Install-Driver.ps1 -InfPath <inf>` on an admin prompt.
