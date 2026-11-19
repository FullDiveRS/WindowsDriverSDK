# Sensor guide

The sensor module (`WindowsDriver.Sdk.Sensor`) models a multi-sensor device:
deterministic simulators produce readings, a device class publishes them over
an IOCTL contract and an event queue, and a background sampling loop runs
while the device is powered.

## Core types

| Type | Role |
| --- | --- |
| `ISensorSource` | a sampled data source: kind, unit, value count, sample rate |
| `AccelerometerSimulator` | gravity on +Z, smooth seeded sine-noise, transient impulses |
| `AmbientLightSimulator` | indoor baseline + day-cycle daylight + flicker, manual override |
| `SensorReading` | (kind, timestamp, values[], unit) |
| `SensorDevice : DeviceBase` | hosts sources, IOCTL contract, event queue, sampling loop |

## Deterministic simulation

Both simulators are **pure functions of the timestamp** — no hidden state, no
random walks. The same timestamp always produces the same values, which makes
tests exact: the VirtualSensor sample asserts gravity ≈ 9.8 m/s² on Z, the
3-minute-past-midnight lux level (indoor baseline, no daylight), and byte-level
round-trips through the IOCTL payloads.

The accelerometer adds *impulses* — transient accelerations with a triangular
envelope over a duration (`AddImpulse(at, duration, (dx, dy, dz))`). A shake
is then a testable event: sample mid-impulse and assert X > 5 m/s².

The ambient light simulator models indoor baseline (400 lx default) with a
daylight sine curve peaking at 12:00 (12,000 lx default) plus slow flicker,
and a manual `OverrideLux` for "the lamp is on" scenarios.

## The IOCTL contract

`SensorIoctlCodes` (vendor functions on `FILE_DEVICE_UNKNOWN`, METHOD_BUFFERED):

- **GetSensorCount** — out: u32.
- **GetSensorInfo(index)** — out: (u32 kind, u32 valueCount, u32 sampleRateMilliHz, char[16] unit). Out-of-range index → `STATUS_INVALID_PARAMETER`.
- **GetLatestReading(index)** — out: (u64 timestamp ticks, u32 valueCount, valueCount × f64). Before the first sample → `STATUS_DEVICE_NOT_READY`.
- **SetSampleRate(index, milliHz)** — adjusts the poll rate of one sensor.

Timestamps are `DateTimeOffset.UtcTicks` — reproducible under the fake clock.

## Polling and push

Clients choose:

- **Poll** — `GetLatestReading` whenever convenient.
- **Subscribe** — `SensorDevice.Readings` is a bounded `Channel<SensorReading>`
  (drop-oldest at 256 by default): every `SampleOnce()` writes one reading per
  sensor. Drop-oldest matches how real sensor stacks behave under load — you
  lose old samples, never block the device.

## The sampling loop

On `D0Entry` the device starts a loop that wakes at the fastest configured
sensor rate (`Task.Delay(interval, TimeProvider, ct)`) and samples everything;
on `D0Exit` it stops and waits (bounded by 2 s). Under the fake clock the loop
advances when you advance time — no real waiting in tests. Tests that need
deterministic single steps call `SampleOnce()` directly (also public), so the
loop is exercised by exactly one test and the rest stay rock solid.

## Writing your device

```csharp
public sealed class MySensorDevice : SensorDevice
{
    public MySensorDevice(DeviceContext context)
        : base(context, new SensorDeviceOptions
        {
            Sources =
            [
                new AccelerometerSimulator(seed: 42, sampleRateHz: 50),
                new AmbientLightSimulator(seed: 7, sampleRateHz: 4),
            ],
        })
    {
    }

    protected override Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        // custom IOCTLs (overrides, calibration), then:
        return base.HandleDeviceControlAsync(request, ct);
    }
}
```

Custom sources implement `ISensorSource` (kind, unit, value count, rate,
`Sample(timestamp)`) — a barometer, humidity, or a derived value computed from
other sensors.

## Integration with the Windows sensor stack

Real Windows sensor drivers register with the **sensor class extension
(SensorCx)** on UMDF: the class extension owns the Windows Sensor API surface
(/categories, report intervals, properties) and your driver fills readings.
The mapping from the SDK model:

- `ISensorSource` ↔ one SensorCx sensor (each has a type GUID and a driver-
  provided friendly name/category).
- `SampleOnce` ↔ SensorCx data notifications (`SensorCx` DDI to publish a
  report after collection).
- `SetSampleRate` ↔ report-interval property (`SENSOR_PROPERTY_REPORT_INTERVAL`,
  milliseconds).
- The event queue ↔ SensorCx's notification model (it also batches/rate-limits).

The simulators stay exactly as they are — you swap only the publishing layer.
The Sensor class GUID used by the sample's manifest is the system-defined one
from Microsoft's "system-defined device setup classes" documentation.
