# WdsKmdfShim — KMDF shim driver skeleton

The kernel-side half of the SDK's KMDF interop story. It owns the real KMDF
device/queue objects (where managed code cannot run) and exposes the buffered
IOCTL protocol implemented by `WindowsDriver.Interop.Kmdf.KmdfControlPlane`.

## What the shim provides

| IOCTL (managed constant)               | Function | Behavior |
| -------------------------------------- | -------- | -------- |
| `KmdfShimProtocol.Ping`                | 0x800    | Returns the `WSHI` magic |
| `KmdfShimProtocol.GetVersion`          | 0x801    | Returns major/minor (1.0) |
| `KmdfShimProtocol.SendCommand`         | 0x802    | Runs a vendor command (stub — fill in `WdsHandleCommand`) |
| `KmdfShimProtocol.ReadEvents`          | 0x803    | Drains the kernel event ring (records: 16-byte header + payload) |
| `KmdfShimProtocol.AcknowledgeEvents`   | 0x804    | Marks consumed events (extend for your retention policy) |

The wire structs in `wds_shim.h` mirror `WindowsDriver.Interop.Kmdf` byte-for-byte
(`KmdfEventRecord` = `WDS_EVENT_RECORD`, `KmdfCommandHeader` = `WDS_COMMAND_HEADER`,
`KmdfResultHeader` = `WDS_RESULT_HEADER`).

## Building

1. Visual Studio 2022/2026 + the matching WDK.
2. New Project → **Kernel Mode Driver, KMDF** → name it `WdsKmdfShim`.
3. Replace the generated sources with `wds_shim.c` and `wds_shim.h`.
4. Build for x64/arm64 (Windows 10 1903+ target platform).
5. Sign the .sys + generate a catalog (see `docs/inf-and-signing.md`), then
   install with `tools/Install-Driver.ps1` and a root-enumerated INF produced by
   `infgen` with `"framework": "kmdf"`.

## Talking to it from C#

```csharp
using WindowsDriver.Interop.Kmdf;

var control = new KmdfControlPlane(@"\\.\WdsKmdfShim0");
control.Open();
control.Ping();
var (major, minor) = control.GetVersion();
var (status, data) = control.SendCommand(0x100 /* your command */, new byte[] { 1, 2, 3 });
foreach (var evt in control.ReadEvents())
{
    Console.WriteLine($"seq={evt.Sequence} id={evt.EventId} bytes={evt.Data.Length}");
}
```

## Extension points

- `WdsHandleCommand` in `wds_shim.c` — vendor commands (IDs ≥ `WDS_COMMAND_FIRST_VENDOR`).
- `WdsPostEvent` — post async completions into the ring; the control plane
  drains them from any .NET process.
- The event ring holds 256 records; tune `WDS_EVENT_RING_CAPACITY`.

This file is a reference implementation — it is not compiled by the SDK's CI
(no WDK on CI runners). Compile it on a machine with the WDK before deploying.
