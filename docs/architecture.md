# Architecture

## The problem being solved

Windows drivers must ultimately be native code: kernel-mode drivers are C (the
CLR cannot run at IRQL), and UMDF drivers are COM classes created by
`WUDFHost.exe`. At the same time, most of what a modern device driver *does* —
protocol parsing, state machines, buffering, throttling, configuration — is
plain application logic that iterates fastest in a managed language with
modern tooling and tests.

The Windows Driver SDK resolves this with a **managed-host architecture**: all
device logic lives in .NET 8 behind framework-shaped abstractions, and thin
native components exist only where the OS requires them.

## Layering

```
┌─────────────────────────────────────────────────────────────────────┐
│ Driver project (yours)                                              │
│   IDriver / DriverBase + DeviceBase subclass                        │
├─────────────────────────────────────────────────────────────────────┤
│ WindowsDriver.Sdk (managed)                                         │
│   Runtime      DriverHost · DeviceBase · IoQueue · IoRequest        │
│                NtStatus · WinIoctl · WireFormat · logging/registry  │
│   Modules      Audio (VirtualAudioDevice) · HID (HidDevice)         │
│                Sensor (SensorDevice)     · Serial (SerialDevice)    │
├─────────────────────────────────────────────────────────────────────┤
│ Hosting (one of three, same lifecycle)                              │
│   1. WindowsDriver.Testing.TestDriverHost   — in-memory, any OS     │
│   2. UMDF shim (C++/COM in WUDFHost)        — user-mode, Windows    │
│   3. KMDF control plane over C shim (.sys)  — kernel reach          │
├─────────────────────────────────────────────────────────────────────┤
│ WindowsDriver.Interop (managed)                                     │
│   UmdfShimContract + ManagedDriverBootstrap (hostfxr entry points)  │
│   KmdfControlPlane (CreateFile/DeviceIoControl client)              │
│   Win32Native P/Invoke declarations                                 │
└─────────────────────────────────────────────────────────────────────┘
```

The layering rule is strict: the SDK core has **no** dependency on Windows —
`WindowsDriver.Sdk.dll` runs on Linux, which is why the entire test suite does.
Only `WindowsDriver.Interop` touches Win32, and only its KMDF client actually
calls it at runtime.

## One lifecycle, three hosts

The WDF callback order is encoded once, in `DriverHost`:

```
DriverEntry → (AddDevice → PrepareHardware → D0Entry)* → DriverUnload
```

`DriverHost.StartAsync` loads your `IDriver` type; `DriverHost.AddDeviceAsync`
runs add + power-up and rolls back cleanly on failure
(D0Exit → StopQueues → ReleaseHardware); `DriverHost.ShutdownAsync` runs the
mirror image. All three hosting paths call exactly this class:

| Host | What it provides | Call-site |
| --- | --- | --- |
| `TestDriverHost` (Testing) | fake clock, fake registry, captured log, in-memory device bus | tests, CI on any OS |
| UMDF shim | the real `IWDF*` COM objects, real buffers from `WdfRequest*`, ETW logging | production, user-mode |
| KMDF shim + control plane | kernel device object, event ring, buffered IOCTLs | production, kernel reach |

This is the central testing guarantee: **the orchestration you test is the
orchestration you deploy.** Only the leaf calls (where bytes come from and
where they go) differ per host.

## IO model

`IoQueue` mirrors the WDF queue dispatch modes:

| SDK | WDF equivalent | Semantics |
| --- | --- | --- |
| `QueueDispatchMode.Sequential` | `WdfIoQueueDispatchSequential` | one request at a time, in order |
| `QueueDispatchMode.Parallel` | `WdfIoQueueDispatchParallel` | concurrent handlers |
| `QueueDispatchMode.Manual` | `WdfIoQueueDispatchManual` | driver pulls from `ManualRequests` channel |

`IoRequest` carries kind (DeviceControl/Read/Write), the IOCTL code, input and
output buffers, and is completed exactly once with an NTSTATUS and an
information count. Completion semantics match
`WdfRequestCompleteWithInformation` — for writes the count is input bytes, for
everything else output bytes.

Handlers are plain `Func<IoRequest, CancellationToken, Task>`; the queue
translates `DriverException.Status` and unhandled exceptions into completion
statuses, so a driver bug degrades to a clean request failure instead of a
crashed host.

## Time is an abstraction

Every time-dependent behavior (audio positions, sensor sampling loops, volume
ramps) takes the host's `System.TimeProvider` (introduced as a first-class API
in .NET 8). Production hosts inject `TimeProvider.System`; tests inject
`FakeTimeProvider` and *advance* time, which fires timers synchronously and
deterministically. This removes the largest source of flaky driver tests —
wall-clock races.

## IOCTL conventions

- Custom IOCTLs use `WinIoctl.VendorControlCode(function, access, deviceType)`
  which enforces the vendor function range (≥ 0x800) and produces the exact
  `CTL_CODE` C-macro bit layout — the same value a C driver would emit and a
  Win32 `DeviceIoControl` caller would pass.
- Payloads are little-endian and written with `WireFormat`, which bounds-checks
  every access and throws `STATUS_BUFFER_TOO_SMALL` on short buffers.
- Every module ships a documented contract (`AudioIoctlCodes`,
  `HidIoctlCodes`, `SensorIoctlCodes`, `SerialIoctlCodes`) — stable, testable,
  and shared verbatim with the KMDF shim headers.

## What stays native, and why

| Component | Language | Reason |
| --- | --- | --- |
| UMDF COM activation (`WUDFHost` calls your driver's `DllGetClassObject`) | C++ | OS contract is COM |
| Kernel-mode driver objects (`WdfDeviceCreate`, IRQL, DMA) | C | CLR cannot run in kernel |
| Everything else | C# | testability, iteration speed, tooling |

The shims are deliberately small: the UMDF shim is callback forwarding plus a
`hostfxr` bootstrap (~a few hundred lines); the KMDF shim implements one IOCTL
protocol (`shim/WdsKmdfShim/`, compiling skeleton included). Both ABIs are
frozen in `WindowsDriver.Interop` so the two sides cannot drift apart silently.

## Comparison with other approaches

| Approach | Managed logic | Kernel reach | Testability | Notes |
| --- | --- | --- | --- | --- |
| **This SDK** | full | via control-plane shim | in-memory, deterministic | open source |
| WDK C/C++ directly | none | full | HLK-based | the Microsoft-supported path |
| Commercial driver kits (e.g. Jungo WinDriver .NET) | partial | via proprietary kernel agent | proprietary | commercial licensing |
| .NET NativeAOT driver experiments | full | none today | — | no kernel CLR story |

## Known limits (design honesty)

- The UMDF C++/COM shim is specified but not shipped compiled — it needs a WDK
  environment and your CLSIDs (see `shim/UmdfShim/README.md`).
- Kernel callbacks that must run at device IRQ levels stay in C; the C# control
  plane talks to them over the shim protocol.
- Buffered IOCTLs only (`METHOD_BUFFERED`) in the SDK contracts; direct/
  neither methods are reachable through `WinIoctl.CtlCode` but the shims do not
  map them yet.
