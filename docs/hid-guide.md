# HID guide

The HID module (`WindowsDriver.Sdk.Hid`) can emit real USB HID report
descriptors byte-by-byte, parse them back for validation, and pack/unpack
input reports against a device state object. The HidGamepad sample uses all of
it.

## HID item encoding (what the builder emits)

HID report descriptors are sequences of *items*. Each short item is:

```
byte = (tag << 4) | (type << 2) | sizeCode
  type:    0 = Main, 1 = Global, 2 = Local
  sizeCode: 0 → 0 data bytes, 1 → 1, 2 → 2, 3 → 4
```

Item values are signed, little-endian, encoded in the smallest size that
holds them (with one exception: zero-valued Main items use the 0-byte form).
The tags the builder uses:

| Main | tag | Global | tag | Local | tag |
| --- | --- | --- | --- | --- | --- |
| Input | 0x8 | Usage Page | 0x0 | Usage | 0x0 |
| Output | 0x9 | Logical Min/Max | 0x1/0x2 | Usage Min/Max | 0x1/0x2 |
| Feature | 0xB | Report Size | 0x7 | | |
| Collection | 0xA | Report Count | 0x9 | | |
| End Collection | 0xC | Report ID | 0x8 | | |

You can see these in the canonical gamepad descriptor bytes: `05 01` (Usage
Page Generic Desktop), `09 05` (Usage Game Pad), `A1 01` (Collection
Application), `85 01` (Report ID 1), `05 09` (Usage Page Button)…

## Building a descriptor

```csharp
byte[] descriptor = HidReportDescriptorBuilder.Create()
    .UsagePage(HidUsagePage.GenericDesktop)
    .Usage(GenericDesktopUsage.GamePad)
    .BeginCollection(HidCollectionType.Application)
    .ReportId(1)
    .Buttons(12)                                              // 12 bits + auto pad to byte
    .Axis(GenericDesktopUsage.X, 16, short.MinValue, short.MaxValue)
    .Axis(GenericDesktopUsage.Y, 16, short.MinValue, short.MaxValue)
    .Axis(GenericDesktopUsage.Z, 16, short.MinValue, short.MaxValue)
    .Axis(GenericDesktopUsage.Rx, 16, short.MinValue, short.MaxValue)
    .Axis(GenericDesktopUsage.Ry, 16, short.MinValue, short.MaxValue)
    .Axis(GenericDesktopUsage.Rz, 16, short.MinValue, short.MaxValue)
    .EndCollection()
    .ToBytes();
```

Convenience blocks:

- `Buttons(count)` — Usage Page Button, Usage Min/Max 1..count, Logical 0..1,
  one-bit fields, **automatic Const padding to a byte boundary**, and restores
  the previous usage page afterwards.
- `Axis(usage, bits, min, max)` — single Data,Variable,Absolute field.
- `PaddingInput(bits)` — explicit padding without consuming a usage.

Rules the builder enforces: report IDs must be 1–255 (0 is reserved), report
sizes 1–32 bits, unit exponents −8..7, and `ToBytes()` throws if the descriptor
does not end with an End Collection.

## Validating what you built

`HidReportDescriptorParser.Parse(descriptor)` walks the items, maintains the
global state stack (Push/Pop included), and returns a summary: per-report-ID
bit totals for Input/Output/Feature, every non-const field with its usage
page/min/max and logical range, collection depth, and whether report IDs are
used. The sample's test asserts exactly the layout you expect:

```
ReportId 1 → 112 input bits = 12 buttons + 4 pad + 6 × 16-bit axes
Wire size  = 1 (report ID) + 14 bytes = 15
```

A parser that re-derives the layout is the cheapest way to catch descriptor
bugs before Windows does.

## The input report on the wire

For the canonical gamepad (`GamepadInputReport`):

```
byte 0      report ID (1)
bytes 1..2  buttons 1..12 in bits 0..11 (LSB first), bits 12..15 zero
bytes 3..14 X, Y, Z, Rx, Ry, Rz as int16 little-endian
```

`GamepadState` is the driver-side object; `GamepadInputReport.Pack(state)` →
15 bytes, `Unpack(bytes)` → state. Round-trip is tested, including the
button-bit mapping (button N = bit N−1).

## The HID device

`HidDevice : DeviceBase` serves the descriptors and the current input report:

| IOCTL | Behavior |
| --- | --- |
| `GetReportDescriptor` | out: the raw descriptor bytes |
| `GetDeviceDescriptor` | out: the 9-byte class descriptor (bLength 9, type 0x21, bcdHID 1.11, country, 1 descriptor, type 0x22, length) |
| `GetInputReportSize` | out: u16 wire size |
| `GetInputReport` | out: current report (from `InputReportFactory`) |
| `SetFeatureReport` / `GetFeatureReport` | feature storage + echo |

Read requests return the current input report — `HidDeviceOptions.
InputReportFactory` is a `Func<byte[]>`, so the owning driver decides how
state becomes wire data (the gamepack sample packs its `GamepadState` under a
lock). Subclass `HidDevice` to add IOCTLs (the sample adds `UpdateState` and
`SetInputState`).

## Integration with the Windows HID stack

Two realistic paths:

1. **UMDF + HID class extension (HidCx)** — the supported user-mode route.
   Your driver registers with HidCx and fills the standard hidclass
   IOCTL/DTM contract (`IOCTL_HID_GET_DEVICE_DESCRIPTOR`,
   `IOCTL_HID_READ_REPORT`, …). The SDK's descriptor bytes are the payload —
   what you plug in is *your* descriptor into HidCx's descriptor DDI, and the
   input report bytes into the report DDI. The class extension then exposes
   the device as a HID device to the system (gamepad in games, etc.).
2. **KMDF shim** — for kernel-only stacks; the report bytes flow over the
   shim protocol and a native HID miniport presents them to hidclass.

Either way, building and validating the descriptor in C# with the parser is
the time-saver: the descriptor you tested is the descriptor you ship.
