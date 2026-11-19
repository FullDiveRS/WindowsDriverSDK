# Testing guide

The SDK's testing story rests on one idea: the same `DriverHost` that the
production shims drive also runs inside `TestDriverHost` — so tests exercise
the real lifecycle (add → prepare → D0 → IO → exit), just with in-memory
inputs and a controllable clock.

## Setting up a host

```csharp
var host = TestDriverHost.Builder()
    .UseFakeTime()                      // FakeTimeProvider starting 2026-01-01
    .WithRegistry(new FakeRegistry(("LogLevel", 3u)))
    .Build();

await host.StartAsync<ContosoButtonDriver>("ContosoButtonService");
var device = await host.AddDeviceAsync("ROOT\\CONTOSOBUTTON\\0000", "Root\\ContosoButton");
// … interact …
await host.ShutdownAsync();
```

The builder gives you:

- `FakeTime` — the `FakeTimeProvider` (null when you chose real time).
- `Log` — a `CapturedLog` recording every entry (level, message, time).
- `Registry` — the `FakeRegistry` the driver reads its `Parameters` from.
- `Devices` — the `TestDevice` list.

## Interacting with a device

`TestDevice` mirrors what an application (or the WDF stack) would do:

```csharp
// IOCTLs — input bytes + output buffer size, returns an IoResult snapshot
var result = await device.SendIoctlAsync(ContosoIoctlCodes.GetState, outputSize: 4);
Check.Status(result, NtStatus.Success);
Check.Bytes(result, 4);
var state = BinaryPrimitives.ReadUInt32LittleEndian(result.Output);

// Reads and writes
var data = await device.ReadAsync(256);
await device.WriteAsync(new byte[] { 1, 2, 3 });
```

Every call runs through the device's real queue — dispatch mode, handler
registration, completion semantics and exception mapping all apply. A test
that hangs more than 5 s throws a `TimeoutException` naming the IOCTL, which
is almost always a driver that forgot to complete a request (a genuine bug you
want surfaced).

`Check` helpers (`Status`, `Success`, `Bytes`) throw `TestCheckException`
with a formatted message — they work with xUnit, NUnit and MSTest alike, which
is why `WindowsDriver.Testing` has no framework dependency.

## The fake clock

`FakeTimeProvider` derives from .NET 8's `System.TimeProvider`. Timers created
through it fire inside `Advance()`:

```csharp
host.FakeTime!.Advance(TimeSpan.FromSeconds(1));     // timers + Task.Delay fire now
host.FakeTime.AdvanceTo(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
```

Rules: time never moves backwards (throws), re-entrant `AdvanceTo` from a
timer callback throws, and periodic timers re-arm per period. Because the SDK's
audio position clock, sensor sampling loop and volume ramps all read
`TimeProvider`, a one-second audio position test is exact:

```csharp
await device.SendIoctlAsync(AudioIoctlCodes.Start);
host.FakeTime!.Advance(TimeSpan.FromSeconds(1));
// GetPosition → exactly 48000 frames at 48 kHz
```

## Log assertions

```csharp
host.Log.Contains("device ROOT\\CONTOSOBUTTON\\0000 added");  // substring, case-insensitive
Assert.False(host.Log.ContainsError());                        // no [ERROR] entries
host.Log.Entries.First(e => e.Level == DriverLogLevel.Info).Message
```

Every device logs with an `[instanceId]` prefix via `ScopedDriverLog`, so
multi-device tests can attribute messages.

## Testing device classes

- **Lifecycle order** — assert on the log or on a trace list: entry → add →
  prepare → D0entry (…→ d0exit → release → unload). The SDK test suite does
  exactly this against a tracing driver.
- **Queue semantics** — a Sequential queue handler that sleeps proves
  one-at-a-time dispatch (`maxConcurrent == 1`); unknown IOCTLs prove the
  `InvalidDeviceRequest` default.
- **Failure paths** — throw `DriverException(NtStatus.X)` from a handler and
  assert the request completes with X; incomplete requests surface as the
  SubmitAsync timeout.
- **Devices that need peers** — `SerialLoopbackPair` composes two devices;
  build them directly with a `DeviceContext` like the serial sample's test.
- **INFs** — each sample test generates its INF from the manifest and runs the
  linter, so a broken manifest fails CI on Linux.

## What tests can't cover

Real OS interaction — actual `WUDFHost` activation, hardware registers, IRQL —
stays outside the unit harness by design. Cover it with: the KMDF shim running
in a VM (the control plane protocol is versioned), HLK for certification, and
Driver Verifier before submission. The unit suite's job is to make the 95% of
driver logic that is plain code fast and deterministic to verify.
