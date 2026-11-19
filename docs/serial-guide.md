# Serial guide

The serial module (`WindowsDriver.Sdk.Serial`) implements a complete virtual
COM port: line configuration with classic UART validation, TX/RX queues,
loopback and null-modem wiring, modem status, break conditions, and counters.

## Core types

| Type | Role |
| --- | --- |
| `SerialLineConfig` | baud / data bits / parity / stop bits / flow control, with wire helpers |
| `SerialDevice : DeviceBase` | the port: queues, IOCTL contract, status |
| `SerialLoopbackPair` | cross-connects two ports like a null-modem cable |
| `TxSink` delegate | receives every transmitted byte (used by the pair) |

## Line configuration and validation

`SerialLineConfig` follows the rules a real 8250-family UART enforces:

- baud 50 .. 1,000,000;
- data bits 5–8;
- **1.5 stop bits only with 5 data bits**; **2 stop bits never with 5 data
  bits**;
- parity None/Odd/Even/Mark/Space; flow None/XonXoff/RtsCts.

`Validate()` throws `DriverException(InvalidParameter)` with a specific
message; `SerialDevice.ApplyConfig` calls it before switching, and the
`SetLineConfig` IOCTL surfaces the failure to the client. The wire format is
8 bytes: (u32 baud, u8 dataBits, u8 parity, u8 stopBits, u8 flow, pad).

## Data paths

Three ways bytes move:

1. **Loopback plug** (`Loopback = true`, the default) — everything transmitted
   (via `Write` requests, the `Transmit` IOCTL, or `SerialDevice.Transmit`)
   comes back on the RX queue immediately. Perfect for self-tests.
2. **Null-modem pair** — create two devices with `Loopback = false` and join
   them: `new SerialLoopbackPair(portA, portB)`. A's TX → B's RX and B's TX →
   A's RX, through the `TxSink` hook. The sample's test sends in both
   directions and asserts nothing loops back to the sender.
3. **Remote simulation** — `InjectReceive(bytes)` writes directly into the RX
   queue, as if the peer sent them; the sample exposes it via a custom IOCTL
   (`SimulateRemoteData`).

Queues are `ByteRingBuffer`s (4096 bytes each by default) with full counters:
`TotalBytesWritten/Read` feed the `GetStatus` IOCTL, and `Purge` clears RX
and/or TX by flags.

## Status and faults

`GetStatus` returns (u64 txBytes, u64 rxBytes, u32 framingErrors, u8 cts,
u8 dsr, u8 breakActive, u8 loopback). Fault injection for tests:

- `InjectFramingError()` — bumps the error counter.
- `AssertBreak(true)` — sets the break flag and injects a framing error, like
  a real break condition on the line.

CTS/DSR are asserted by default; expose setters if your scenario needs
handshake failures.

## The IOCTL contract

`SerialIoctlCodes` (vendor functions on `FILE_DEVICE_SERIAL_PORT`,
METHOD_BUFFERED): `GetLineConfig`, `SetLineConfig`, `Transmit`, `Purge`
(bit 0 RX, bit 1 TX), `GetStatus`, `GetRxCount`. Empty `Transmit` payloads are
rejected with `STATUS_INVALID_PARAMETER`, short buffers with
`STATUS_BUFFER_TOO_SMALL`.

## Integration with the Windows serial stack

- **Presenting a COM port to applications** — a driver for the *Ports* class
  that creates a serial device interface gets a COM number from Windows
  (serenum/serial enumeration). A UMDF serial driver implements the serial
  controller contract; the byte paths here map onto its read/write requests.
- **USB CDC-ACM** — a very common real-world shape: the driver is a CDC host
  function driver and the "line" is a USB bulk pair; the config/validation
  logic and the loopback/pair model are directly reusable for testing.
- **Virtual pairs for applications** — many products ship virtual COM pairs
  (com0com-style); `SerialLoopbackPair` is exactly that, in-process for tests
  or across the shim protocol for two real processes.

The sample manifest uses the Ports class GUID
(`{4d36e978-e325-11ce-bfc1-08002be10318}`).
