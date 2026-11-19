# HidGamepad sample

A virtual 6-axis + 12-button HID gamepad. The report descriptor is emitted by
the SDK's real USB HID item encoder (`HidReportDescriptorBuilder`), so the
bytes are spec-compliant and can be validated by parsing them back — the tests
do exactly that.

## What it demonstrates

- **Descriptor engineering** — `HidDescriptors.GamepadSixAxisTwelveButtons()`
  builds the descriptor: report ID 1, 12 button bits + 4 padding bits, then
  X/Y/Z/Rx/Ry/Rz as 16-bit logical axes. 15-byte input reports on the wire.
- **Report packing** — `GamepadState` → `GamepadInputReport.Pack()` /
  `Unpack()` round-trips, button-bit mapping included.
- **Class descriptor** — the 9-byte HID class descriptor announces the report
  descriptor length (bDescriptorType 0x21/0x22).
- **State injection** — `GamepadIoctlCodes.UpdateState` (mask + six int16
  axes) and `SetInputState` (full 15-byte report) simulate the physical
  controls from the client side.
- **Feature reports** — SetFeatureReport/GetFeatureReport echo, like real HID
  devices.

## Layout

```
Driver/SampleGamepadDriver.cs   driver + device (extends HidDevice)
manifest.json                   InfGen manifest → INF (HIDClass, UMDF)
Tests/HidGamepadSampleTests.cs  7 tests: descriptor parse, reports, IOCTLs, INF
```

## Try it

```bash
dotnet test samples/HidGamepad/Tests
infgen generate --manifest samples/HidGamepad/manifest.json
```

## Going further

Read `docs/hid-guide.md` for the item encoding, the byte layout of the input
report, and the two integration paths with the Windows HID stack (HidCx via a
UMDF HID mini-driver, or the KMDF shim path).
