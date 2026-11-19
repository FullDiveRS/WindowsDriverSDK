# INF and signing

Every driver package needs an INF. The SDK generates it from a JSON manifest
with `infgen`, and this guide covers the manifest schema, what the generated
INF contains, and how to get the package signed and installed.

## The manifest

```json
{
  "name": "VirtualAudio",
  "provider": "Contoso Devices",
  "manufacturer": "Contoso Devices LLC",
  "version": "0.1.0",
  "driverVerDate": "11/18/2026",
  "class": "Media",
  "classGuid": "{4d36e96c-e325-11ce-bfc1-08002be10318}",
  "framework": "umdf",
  "serviceName": "WdsVirtualAudio",
  "catalogFile": "VirtualAudio.cat",
  "files": [
    { "source": "VirtualAudio.Driver.dll" }
  ],
  "models": [
    {
      "name": "Contoso Virtual Audio Device (SDK sample)",
      "hardwareIds": ["Root\\ContosoVirtualAudio"],
      "compatibleIds": ["*WdsVirtualAudio"]
    }
  ],
  "wdfLibraryVersion": "2.15.0"
}
```

Field notes:

- `class`/`classGuid` — the **setup class**. Common values: Media
  `{4d36e96c-e325-11ce-bfc1-08002be10318}` (sound/game controllers), HIDClass
  `{745a17a0-74d3-11d0-b6fe-00a0c90e91ab}`, Ports
  `{4d36e978-e325-11ce-bfc1-08002be10318}`, Sensor
  `{5175d334-c371-4806-b3ba-71fd53c9258d}`. The full list lives in Microsoft's
  "system-defined device setup classes" documentation.
- `framework` — `umdf` or `kmdf`; picks the `[<install>.Wdf]` wiring
  (`UmdfService` + `UmdfLibraryVersion` + `ServiceBinary` vs. `KmdfService` +
  `KmdfLibraryVersion`).
- `hardwareIds` — most specific first. Root-enumerated virtual devices
  typically use `Root\YourDevice`.
- `driverVerDate` — MM/DD/YYYY; defaults to the generation date.

`infgen validate --inf file.inf` lints the result: required sections,
parseable GUIDs, DriverVer format, duplicate sections, model→install-section
references, and unresolved string keys (comments stripped first). Run InfVerif
from the WDK before submission — it knows rules this linter does not.

## What the generated INF contains

```
[Version]                    Signature, Class, ClassGuid, Provider,
                             CatalogFile, DriverVer, PnpLockdown=1
[Manufacturer]               %Manufacturer%=Standard,NTamd64,NTarm64
[Standard.NTamd64]           %Model0%=Install.NTamd64,<hwids>
[Install.NTamd64]            CopyFiles=DriverFiles
[Install.NTamd64.Wdf]        UmdfService=<svc>, <svc>_Install
                             UmdfServiceOrder=<svc>
[<svc>_Install]              UmdfLibraryVersion=2.15.0
                             ServiceBinary=%12%\<driver.dll>
[DriverFiles]                the file list
[SourceDisksNames/Files]     package media mapping
[DestinationDirs]            DriverFiles = 12
[Strings]                    Provider, Manufacturer, Model0, DiskName
```

Everything is deterministic from the manifest, so you can regenerate instead
of hand-editing (the header comment says exactly that).

## Signing: test signing

For development:

1. Create a test certificate (elevated PowerShell):

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=WDS Test" `
    -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
    -Provider "Microsoft Enhanced Cryptographic Provider v1.0" `
    -CertStoreLocation Cert:\CurrentUser\My
Export-PfxCertificate -Cert $cert -FilePath .\test.pfx
Export-Certificate -Cert $cert -FilePath .\test.cer
```

2. Sign the binaries and package with `signtool` (from the WDK):

```powershell
signtool sign /fd SHA256 /a /f .\test.pfx .\YourDriver.dll
Inf2Cat /driver:. /os:10_X64        # builds the .cat from the INF + files
signtool sign /fd SHA256 /a /f .\test.pfx .\YourDriver.cat
```

3. Trust the certificate and enable test signing (reboot required):

```powershell
Import-Certificate -FilePath .\test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
.\tools\Enable-TestSigning.ps1      # bcdedit /set testsigning on
```

4. Install:

```powershell
.\tools\Install-Driver.ps1 -InfPath .\YourDriver.inf -CertPath .\test.cer
```

## Signing: production (attestation / WHQL)

- **Attestation signing** — sign with an EV code-signing certificate, submit
  the package to the Windows hardware Dev Center, and Microsoft countersigns
  it; the countersigned package installs everywhere without test signing.
  Kernel drivers require Microsoft's signature (attestation or full WHQL) —
  a self-signed kernel package will not load on retail.
- **WHQL** — full certification via HLK (Windows Hardware Lab Kit) tests;
  required for some driver types and wanted for Windows Update distribution.
- Regardless of path: `InfVerif`, `SDV` (static driver verifier, KMDF only),
  and `Driver Verifier` at runtime are your pre-submission gates.

## Install/uninstall scripts

`tools/Install-Driver.ps1` wraps `pnputil /add-driver /install` with admin and
test-signing prechecks and optional certificate import.
`tools/Uninstall-Driver.ps1` maps your INF name to its published `oemXX.inf`
name, removes matching devices, and deletes the package
(`pnputil /delete-driver /uninstall /force`).

## Common pitfalls

- **GUID braces** — the manifest accepts `{guid}` or bare `guid`; `infgen`
  normalizes to lowercase braced form.
- **Class mismatch** — the Class/ClassGuid pair must be consistent (e.g. HID
  drivers install into HIDClass; putting a sensor driver in Ports will confuse
  the stack).
- **Files must exist at install time** — `CopyFiles` sections reference files
  that must ship in the same package directory; the catalog covers exactly the
  files the INF lists.
- **Kernel + test signing** — a test-signed kernel driver loads only while
  test signing is enabled; plan the attestation submission early.
