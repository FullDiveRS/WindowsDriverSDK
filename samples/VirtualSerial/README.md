# VirtualSerial sample

A virtual COM port: line configuration with classic UART validation, TX/RX
queues, loopback, modem status (CTS/DSR), break detection, counters — and a
null-modem pair that cross-connects two ports like a cable.

## What it demonstrates

- **Line config** — `SerialLineConfig` validates baud (50–1,000,000), data
  bits (5–8), parity, stop bits (the 1.5-stop-bits-only-with-5-data-bits rule)
  and flow control before applying, exactly like a real UART would.
- **Data paths** — `Write`/`Transmit` into the TX queue; loopback mode echoes
  back on RX; `SerialLoopbackPair` wires device A's TX to device B's RX and
  vice versa (both created with `Loopback = false`).
- **Status** — `GetStatus` reports TX/RX counters, framing errors, CTS/DSR,
  break-active and loopback state; `AssertBreak`/`InjectFramingError` simulate
  line faults for tests.
- **Extensibility** — `SampleSerialDevice` adds `SimulateRemoteData` (inject
  bytes into RX as if a remote peer sent them).

## Layout

```
Driver/SampleSerialDriver.cs    driver + device (extends SerialDevice)
manifest.json                   InfGen manifest → INF (Ports class, UMDF)
Tests/VirtualSerialSampleTests.cs  7 tests: loopback, config, purge, pair, INF
```

## Try it

```bash
dotnet test samples/VirtualSerial/Tests
infgen generate --manifest samples/VirtualSerial/manifest.json
```

## Going further

Read `docs/serial-guide.md` for the Windows serial stack (serenum, serial.h
IOCTLs, CDC-ACM over USB) and how to expose the port to applications as a real
COM device.
