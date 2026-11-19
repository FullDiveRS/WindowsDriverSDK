# VirtualSensor sample

A sensor suite device: a 3-axis accelerometer and an ambient light sensor, both
driven by deterministic simulators from the SDK, published over an IOCTL
contract and an event queue.

## What it demonstrates

- **Pure-function simulation** — `AccelerometerSimulator` (gravity on +Z,
  smooth seeded noise, transient impulses) and `AmbientLightSimulator` (indoor
  baseline + day cycle + flicker) are functions of the timestamp, so tests are
  fully reproducible.
- **Polling and push** — clients poll `SensorIoctlCodes.GetLatestReading`, or
  subscribe to `SensorDevice.Readings` (a bounded channel with drop-oldest
  semantics, like a real sensor event queue).
- **Background sampling** — while in D0 the device samples at the fastest
  sensor rate using `Task.Delay(interval, TimeProvider)`, so a fake clock can
  drive it in tests.
- **Extensibility** — `SampleSensorDevice` adds `SetAmbientOverride` (force a
  lux value, e.g. simulating a lamp) and `InjectImpulse` (a shake) on top of
  the sensor contract.

## Layout

```
Driver/SampleSensorDriver.cs    driver + device (extends SensorDevice)
manifest.json                   InfGen manifest → INF (Sensor class, UMDF)
Tests/SensorSampleTests.cs      6 tests: sensors, determinism, IOCTLs, events, INF
```

## Try it

```bash
dotnet test samples/VirtualSensor/Tests
infgen generate --manifest samples/VirtualSensor/manifest.json
```

## Going further

Read `docs/sensor-guide.md` for the SensorCx (sensor class extension) mapping —
real Windows sensor drivers register with SensorCx and report through the
Windows Sensor API; the simulators and the IOCTL contract stay the same.
