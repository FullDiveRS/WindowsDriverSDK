# UmdfShim — native UMDF host shim (design notes)

UMDF drivers are COM objects running inside `WUDFHost.exe`. Microsoft's WDK
expects C++/COM implementations; the SDK runs your C# driver logic inside that
host through a thin native shim.

## Architecture

```
WUDFHost.exe
 └── WdsShimU.dll (C++/COM — you build this with the WDK)
      ├─ implements IDriverEntry (IWDFDriver/IWDFDevice callbacks, real IIDs from wudfddi.h)
      ├─ hosts the .NET runtime via hostfxr (hostfxr_initialize_for_runtime_config,
      │    hostfxr_get_runtime_delegate → load_assembly_and_get_function_pointer)
      └─ forwards every callback to WindowsDriver.Interop.Umdf.ManagedDriverBootstrap
           (WdsInitialize / WdsDeviceAdd / WdsQueueRequest / WdsDeviceD0Entry /
            WdsDeviceD0Exit / WdsDeviceRelease / WdsDriverUnload)

Your driver (C#)
 └─ implements WindowsDriver.Sdk.IDriver / DeviceBase — the same code you tested
    with TestDriverHost.
```

The ABI between the two sides is frozen in two places that must stay in sync:

- `WindowsDriver.Interop.Umdf.UmdfShimContract.ShimHeader` (C header text)
- `WindowsDriver.Interop.Umdf.ManagedDriverBootstrap` (`[UnmanagedCallersOnly]` entry points)

## Implementation checklist for the C++ shim

1. **DllGetClassObject / class factory** — standard COM scaffolding so
   `WUDFHost` can create the driver object (CLSID from your INF
   `UmdfService` registration).
2. **IDriverEntry** — `OnInitialize` passes service name + registry path into
   `WDS_SHIM_SETTINGS`; `OnDeviceAdd` collects hardware IDs (via
   `IWDFPropertyStore`/`WdfDevice` properties) and calls `WdsDeviceAdd`;
   `OnDeinitialize` calls `WdsDriverUnload`.
3. **Runtime hosting** — locate `hostfxr.dll` next to your shim, resolve
   `hostfxr_initialize_for_runtime_config` and
   `hostfxr_get_runtime_delegate`, load the managed assembly's
   `.runtimeconfig.json`, get a function pointer to each `Wds*` entry point.
   Microsoft documents this flow as "Write a custom .NET host".
4. **Queue plumbing** — create `IWDFIoQueue` per device; on each request,
   marshal `WdfRequestRetrieveInputBuffer`/`OutputBuffer` into
   `WdsQueueRequest`; complete with the shim's `CompleteRequest` callback
   (which the managed side invokes with `WdfRequestCompleteWithInformation`
   semantics — for writes the information is input bytes).
5. **Power/PnP** — map `IWDFDriverCreatedDevice` callbacks to
   `WdsDeviceD0Entry` / `WdsDeviceD0Exit` / `WdsDeviceRelease`.
6. **Logging** — the shim's `Log` callback receives UTF-8 lines from the
   managed host and should forward them to `WUDF` tracing / `WPP`.

## Files

- `wds_shim_abi.h` — copy of the contract header (source of truth lives in
  `WindowsDriver.Interop`, so the SDK can keep both sides aligned).

## Status

The SDK ships the full managed side and the exact ABI; the C++/COM shim above
is intentionally left as an implementation task for your driver project — it is
mechanical forwarding code, but it needs a WDK build environment and your
product's COM CLSIDs/INF to be meaningful. The KMDF path
(`shim/WdsKmdfShim/`) has a compilable C skeleton today if you need kernel
reach sooner.
