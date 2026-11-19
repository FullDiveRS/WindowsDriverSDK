# KMDF interop guide — C# control plane over a kernel shim

Kernel-mode drivers are C territory: the CLR cannot run at IRQL, and KMDF DDIs
take kernel object handles. Any claim of "kernel drivers in C#" is false
advertising. What *is* achievable — and what this SDK provides — is the split
that production teams actually use:

- **Kernel side (C, small, frozen)**: device objects, queues, DMA, interrupts —
  the hot path and whatever must touch hardware.
- **Managed side (C#, everything else)**: policy, configuration, state
  aggregation, telemetry, UI, testing — running in a normal .NET process and
  talking to the kernel driver through a documented IOCTL protocol.

This guide covers the second half: the `WindowsDriver.Interop.Kmdf` control
plane and the shim driver skeleton in `shim/WdsKmdfShim/`.

## The shim protocol

The shim driver (`WdsKmdfShim.sys`, KMDF) exposes a device interface and
implements five buffered IOCTLs — the same contract as
`WindowsDriver.Interop.Kmdf.KmdfShimProtocol`:

| IOCTL | Function | In | Out |
| --- | --- | --- | --- |
| `Ping` | 0x800 | — | u32 magic `WSHI` (0x49485357) |
| `GetVersion` | 0x801 | — | (u32 major, u32 minor) = 1.0 |
| `SendCommand` | 0x802 | `KmdfCommandHeader` + data | `KmdfResultHeader` + data |
| `ReadEvents` | 0x803 | — | (u32 count) + `KmdfEventRecord`s |
| `AcknowledgeEvents` | 0x804 | u32 eventMask | — |

Wire structs are `#pragma pack(1)` and mirrored byte-for-byte on both sides
(`KmdfEventRecord` ↔ `WDS_EVENT_RECORD`, etc. — verified by unit tests that
assert `Marshal.SizeOf` against the C `C_ASSERT` values: 16-byte event record,
8-byte command and result headers).

Vendor commands start at `KmdfCommandIds.FirstVendorCommand` (0x100); IDs
below are reserved for the shim itself. `SendCommand` is synchronous: the
kernel runs your command and returns a status plus payload. Asynchronous
results flow back as events: kernel code calls `WdsPostEvent(device, eventId,
size, data)`, the ring holds up to 256 records, and the control plane drains
them with `ReadEvents`.

## Using the control plane from C#

```csharp
using WindowsDriver.Interop.Kmdf;

var control = new KmdfControlPlane(@"\\.\WdsKmdfShim0");
control.Open();                       // CreateFileW on the device interface

control.Ping();                       // verifies the WSHI magic
var (major, minor) = control.GetVersion();

// Send a vendor command defined on the kernel side.
var (status, data) = control.SendCommand(0x100, new byte[] { 0x01, 0x02 });
if (status.IsSuccess())
{
    // parse `data`
}

// Drain the kernel event ring (e.g. on a timer).
foreach (KmdfEvent evt in control.ReadEvents(maxEvents: 64))
{
    Console.WriteLine($"seq={evt.Sequence} id={evt.EventId} bytes={evt.Data.Length}");
}

control.Dispose();
```

`Open()` uses the real `CreateFileW`/`DeviceIoControl` P/Invoke declarations in
`Win32Native` — on a machine without the driver installed it throws the actual
Win32 error (`ERROR_FILE_NOT_FOUND`), which is your cue that the package is
not deployed.

## Building the kernel shim

`shim/WdsKmdfShim/` is a compiling-against-WDK skeleton:

- `wds_shim.h` — protocol constants, packed wire structs (`C_ASSERT`s pin the
  sizes), the device-interface GUID, and the ring capacity.
- `wds_shim.c` — `DriverEntry`, `EvtDeviceAdd` (device + sequential queue +
  interface creation), `EvtIoDeviceControl` implementing all five IOCTLs, an
  event ring with a spin lock, and `WdsHandleCommand` — a stub where your
  kernel work goes.
- Build in Visual Studio with the WDK (KMDF, Windows 10+, x64/arm64), sign, and
  install with `tools/Install-Driver.ps1` using an INF generated with
  `"framework": "kmdf"` (InfGen emits the `KmdfService` wiring).

The skeleton is deliberately not built by CI — GitHub runners have no WDK.
Compile it on a machine with the WDK before deployment (see
[ci-cd.md](ci-cd.md) for the pipeline shape).

## What belongs on each side

| Put in the kernel (C) | Put in C# (control plane) |
| --- | --- |
| register access, DMA, ISR/DPC work | configuration and policy |
| IRQL-sensitive sequencing | state aggregation and interpretation |
| small fixed command handlers | all business logic and protocol parsing |
| event emission (`WdsPostEvent`) | event interpretation, UI, storage |

The SDK's device-class modules (audio/HID/sensor/serial) are control-plane
friendly: their IOCTL contracts run unmodified over the shim protocol, so you
can develop the full logic with `TestDriverHost` and later connect the same
client code to the kernel device.

## Testing without a kernel

The control plane's wire layer is testable without a device: the unit tests
assert IOCTL code uniqueness and vendor-range discipline, struct sizes, and
the magic value. `KmdfControlPlane` itself guards every call with "open first"
failures. For end-to-end behavior, run the shim in a Windows VM and point the
control plane at it — the protocol is versioned via `GetVersion`, so both
sides can negotiate.

## When to skip the shim entirely

If your "driver" is really a virtual device (no hardware), prefer UMDF — the
UMDF path runs entirely in user mode and `TestDriverHost` covers more of it.
The KMDF story earns its complexity only when you must touch actual hardware
or kernel APIs.
