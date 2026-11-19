# Getting started

This guide takes you from an empty folder to a running, tested, deployable
driver. Everything except the final deployment steps works on any OS — Linux
and macOS are fine for development because the test host runs the driver logic
in memory.

## 1. Prerequisites

| Component | Needed for | Where |
| --- | --- | --- |
| .NET 8 SDK (8.0.4xx+) | everything | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Windows 10/11 x64 or ARM64 | deploying drivers | — |
| WDK (matching your VS version) | building the native shims, InfVerif, signing | [learn.microsoft.com/windows-hardware/drivers/download-the-wdk](https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk) |
| Visual Studio 2022/2026 (or the EWDK) | building `shim/WdsKmdfShim` | — |

You do **not** need Windows for development: the `TestDriverHost` runs driver
logic in-process with a controllable clock, which is where you will spend most
of your iteration time.

## 2. Get the packages

The SDK is distributed as five NuGet packages (see the repository `artifacts/`
folder, or your private feed):

```bash
dotnet new install WindowsDriver.Templates --prerelease
dotnet tool install --prerelease --tool-path ./.tools WindowsDriver.InfGen
```

When building from this repository, point `--add-source` at the local
`artifacts/` folder:

```bash
./build.sh            # restores, builds, tests and packs everything
dotnet new install ./artifacts/WindowsDriver.Templates.0.1.0-preview.1.nupkg
```

## 3. Create your first driver

```bash
dotnet new driver-solution -n ContosoButton
cd ContosoButton
dotnet new sln -n ContosoButton
dotnet sln add ContosoButton/ContosoButton.csproj ContosoButton.Tests/ContosoButton.Tests.csproj
dotnet test
```

The template produces:

- `ContosoButton/ContosoButton.cs` — the driver: an `IDriver` implementation
  (`DriverBase` base class) and a `DeviceBase` subclass with an IOCTL
  dispatcher.
- `ContosoButton.Tests/ContosoButtonTests.cs` — an xUnit test that boots the
  driver in `TestDriverHost`, adds a device, and asserts behavior.
- `ContosoButton.manifest.json` — the InfGen manifest that generates the INF.

## 4. Understand the two entry points

A driver implements `IDriver` (usually via `DriverBase`):

```csharp
public sealed class ContosoButtonDriver : DriverBase
{
    public override Task OnDriverEntryAsync(DriverContext context, CancellationToken ct)
    {
        context.Log.Info("entering, service '{0}'", context.DriverServiceName);
        return Task.CompletedTask;
    }

    public override Task<DeviceBase> OnDeviceAddAsync(
        DeviceAddContext addContext, DeviceContext deviceContext, CancellationToken ct)
    {
        var device = new ContosoButtonDevice(deviceContext);
        deviceContext.CreateDeviceInterface(new Guid("…")); // apps open this
        return Task.FromResult<DeviceBase>(device);
    }
}
```

`OnDriverEntryAsync` runs once (allocate driver-wide state, read the service
`Parameters` registry key via `context.Registry`). `OnDeviceAddAsync` runs per
device the PnP manager enumerates — create your `DeviceBase` subclass and
return it. The host then runs the WDF power-up sequence:
`PrepareHardware` → `D0Entry`.

## 5. Handle IOCTLs

Device-specific work happens in queue handlers. The default queue is created
for you (parallel dispatch); create more with `CreateQueue`:

```csharp
public sealed class ContosoButtonDevice : DeviceBase
{
    public ContosoButtonDevice(DeviceContext context) : base(context)
    {
        DefaultQueue.OnDeviceControl(HandleDeviceControlAsync);
    }

    private Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        var code = request.IoControlCode;
        if (code == ContosoIoctlCodes.GetButtonState)   // VendorControlCode(0x800, FileAccessRead)
        {
            WireFormat.WriteUInt32(request.OutputBuffer.Span, 0, _state);
            request.Complete(NtStatus.Success, sizeof(uint));
        }
        else
        {
            request.Fail(NtStatus.InvalidDeviceRequest); // unknown IOCTL
        }
        return Task.CompletedTask;
    }
}
```

Rules that match WDF semantics:

- Complete every request **exactly once** — double completion throws (in real
  WDF it bugchecks).
- For writes, `Complete(status, bytes)` counts bytes taken from the *input*
  buffer; for reads/IOCTLs, bytes placed in the *output* buffer.
- Throw `DriverException(NtStatus.…)` to fail a request with a specific
  status; uncaught exceptions complete with `STATUS_UNSUCCESSFUL`.

## 6. Test it

```csharp
[Fact]
public async Task Button_state_roundtrips()
{
    var host = TestDriverHost.Builder().UseFakeTime().Build();
    await host.StartAsync<ContosoButtonDriver>("ContosoButtonService");
    var device = await host.AddDeviceAsync("ROOT\\CONTOSOBUTTON\\0000", "Root\\ContosoButton");

    var result = await device.SendIoctlAsync(ContosoIoctlCodes.GetButtonState);
    Check.Status(result, NtStatus.Success);

    await host.ShutdownAsync();
}
```

`TestDriverHost` runs the exact lifecycle the production shims run, so behavior
you verify here is the behavior you get on Windows. See
[testing-guide.md](testing-guide.md) for the fake clock, fake registry and
log assertions.

## 7. Generate the INF and deploy

```bash
./.tools/infgen generate --manifest ContosoButton/ContosoButton.manifest.json
# ContosoButton.inf

infgen validate --inf ContosoButton.inf        # lint before signing
```

On a Windows machine (with the package signed — see
[inf-and-signing.md](inf-and-signing.md)):

```powershell
.\tools\Enable-TestSigning.ps1      # once, for test-signed packages (reboot)
.\tools\Install-Driver.ps1 -InfPath .\ContosoButton.inf -CertPath .\test.cer
.\tools\Uninstall-Driver.ps1 -InfName ContosoButton.inf
```

## 8. Debugging

- **In tests** — pass a `CapturedLog` to the host builder and assert on its
  entries; every device logs with its instance-id prefix.
- **On Windows (UMDF)** — enable WUDFHost tracing with
  `Wudfhost.exe` debug settings; the shim forwards your `IDriverLog` lines to
  ETW. `!wdfkd.wdflogdump` shows the framework logs in WinDbg.
- **On Windows (KMDF control plane)** — `KmdfControlPlane.ReadEvents()` drains
  the kernel ring; post events from the shim's `WdsPostEvent` for tracing.

## Next steps

- Pick the hosting path: [umdf-guide.md](umdf-guide.md) (user-mode, first
  choice) or [kmdf-interop-guide.md](kmdf-interop-guide.md) (kernel reach).
- Look at the sample closest to your device class: [audio](../samples/VirtualAudio/),
  [HID](../samples/HidGamepad/), [sensor](../samples/VirtualSensor/),
  [serial](../samples/VirtualSerial/).
