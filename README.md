# Windows Driver SDK for .NET

Write Windows device drivers in C#. The SDK gives you a managed driver runtime
with the WDF programming model — driver/device lifecycle, IO queues, IOCTL
dispatch, NTSTATUS semantics — plus ready-made device-class modules for
**audio**, **HID**, **sensor** and **serial** devices, an INF generator, a
test host that runs your driver without Windows, and the native shim contracts
that take the same code to production.

```
┌────────────────────────────────────────────────────────────┐
│  Your driver: IDriver + DeviceBase subclass in pure C#     │
├────────────────────────────────────────────────────────────┤
│  WindowsDriver.Sdk                                         │
│   • runtime: lifecycle, IoQueue, IOCTL, NTSTATUS, logging  │
│   • modules: Audio · HID · Sensor · Serial                 │
├────────────────────────────────────────────────────────────┤
│  Hosting (choose one)                                      │
│   • WindowsDriver.Testing.TestDriverHost  (unit tests)     │
│   • UMDF shim  (user-mode, WUDFHost)                      │
│   • KMDF control plane over the C shim (kernel reach)      │
└────────────────────────────────────────────────────────────┘
```

## Why

Microsoft's Windows Driver Kit supports C/C++ for driver code (UMDF drivers are
C++/COM; kernel drivers are C). Managed code has never been officially
supported in kernel mode, and UMDF is C++-only in the box. This SDK fills the
gap the practical way:

- **Driver logic in C#** — the parts that iterate quickly (device logic, state
  machines, protocol handling, policy) run in .NET 8.
- **Native where required** — the parts that must be native (COM activation in
  WUDFHost, kernel-mode DDIs) are thin, frozen-ABI shims documented and
  scaffolded in this repository (`shim/`).
- **Testable without Windows** — `TestDriverHost` runs your driver with a fake
  clock and in-memory bus on any OS. The full sample suite (62 tests) runs on
  Linux CI in under a second.

This is the same architecture commercial products (e.g. Jungo WinDriver's
user-mode .NET offering) and many in-house drivers use — but open, and with the
WDF-shaped API surface you already know.

## Packages

| Package | What it gives you |
| --- | --- |
| `WindowsDriver.Sdk` | Core runtime + Audio/HID/Sensor/Serial modules |
| `WindowsDriver.Interop` | UMDF shim contracts, KMDF control plane, Win32 P/Invoke |
| `WindowsDriver.Testing` | `TestDriverHost`, `FakeTimeProvider`, captured logging |
| `WindowsDriver.InfGen` | `infgen` dotnet tool: manifest → INF, INF lint |
| `WindowsDriver.Templates` | `dotnet new driver-umdf` / `driver-tests` / `driver-solution` |

## Quickstart

Prerequisites: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
Building/deploying a real driver additionally needs the
[WDK](https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk)
on a Windows machine.

```bash
# 1. Create a driver project from the template
dotnet new install WindowsDriver.Templates --prerelease
dotnet new driver-solution -n ContosoButton
cd ContosoButton && dotnet new sln -n ContosoButton
dotnet sln add ContosoButton/ContosoButton.csproj ContosoButton.Tests/ContosoButton.Tests.csproj

# 2. Test it — no Windows, no device, no VM
dotnet test

# 3. Generate the INF for deployment
dotnet tool install --prerelease --tool-path ./.tools WindowsDriver.InfGen
./.tools/infgen generate --manifest ContosoButton/ContosoButton.manifest.json
```

That is a complete driver: a `DeviceBase` subclass with IOCTL handlers, a
passing test, and an INF you can sign and deploy.

## Sample drivers (all with tests + INF manifests)

| Sample | Class | Highlights |
| --- | --- | --- |
| [VirtualAudio](samples/VirtualAudio/) | Media | PCM ring buffer, 44.1/48/96 kHz format negotiation, volume ramps, tone generator |
| [HidGamepad](samples/HidGamepad/) | HIDClass | Real USB HID report descriptor builder (6-axis + 12 buttons), report pack/unpack |
| [VirtualSensor](samples/VirtualSensor/) | Sensor | Accelerometer + ambient light simulators, IOCTL + event-queue publishing |
| [VirtualSerial](samples/VirtualSerial/) | Ports | Line config validation, loopback, null-modem pairs, break/framing faults |

## Documentation

| Doc | Content |
| --- | --- |
| [Getting started](docs/getting-started.md) | Install, first driver end-to-end, deploy |
| [Architecture](docs/architecture.md) | The managed-host model, layering, hosting paths |
| [UMDF guide](docs/umdf-guide.md) | Hosting C# drivers in WUDFHost, shim ABI, lifecycle mapping |
| [KMDF interop guide](docs/kmdf-interop-guide.md) | Kernel constraints, control plane, the C shim protocol |
| [Audio guide](docs/audio-guide.md) | Ring buffer, negotiation, the audio IOCTL contract |
| [HID guide](docs/hid-guide.md) | HID item encoding, descriptor building, report layout |
| [Sensor guide](docs/sensor-guide.md) | Simulators, sampling loop, SensorCx mapping |
| [Serial guide](docs/serial-guide.md) | Line config, loopback pairs, Windows serial stack |
| [INF and signing](docs/inf-and-signing.md) | Manifest schema, INF anatomy, test-signing, attestation |
| [Testing guide](docs/testing-guide.md) | TestDriverHost, fake clock, patterns |
| [CI/CD](docs/ci-cd.md) | GitHub Actions setup, WDK limitations on runners |
| [API reference](docs/api-reference.md) | Type-by-type summary of the SDK surface |

## Repository layout

```
src/WindowsDriver.Sdk        core runtime + device-class modules
src/WindowsDriver.Interop    UMDF/KMDF interop, Win32 P/Invoke
src/WindowsDriver.Testing    test host helpers
src/WindowsDriver.InfGen     infgen tool
src/WindowsDriver.Templates  dotnet new templates
samples/                     VirtualAudio, HidGamepad, VirtualSensor, VirtualSerial
tests/                       SDK unit tests
shim/WdsKmdfShim             KMDF shim driver skeleton (C, build with the WDK)
shim/UmdfShim                UMDF COM shim design notes + ABI
tools/                       Install-Driver.ps1, Uninstall-Driver.ps1, Enable-TestSigning.ps1
docs/                        the documentation set
.github/workflows/ci.yml     build + test + pack + smoke tests
```

## Status and support

Version 0.1.0-preview.1. The managed SDK surface is complete and tested (62
tests, all green). The KMDF C shim compiles as a WDK skeleton and implements
the full IOCTL contract; the UMDF C++/COM shim is specified and ABIs frozen,
with implementation notes in `shim/UmdfShim/README.md`.

License: MIT (see [LICENSE](LICENSE)).
