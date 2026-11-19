// wds_shim.h — wire contract for the WDS KMDF shim driver.
// Mirrors WindowsDriver.Interop.Kmdf (KmdfShimProtocol) — keep both in sync.
#pragma once

#include <ntddk.h>
#include <wdf.h>

// The shim answers PING with this magic (ASCII "WSHI", little-endian).
#define WDS_SHIM_MAGIC          0x49485357u
#define WDS_SHIM_VERSION_MAJOR  1u
#define WDS_SHIM_VERSION_MINOR  0u

// IOCTL function codes (CTL_CODE(FILE_DEVICE_UNKNOWN, function, METHOD_BUFFERED, access)).
#define WDS_IOCTL_PING              CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_READ_ACCESS)
#define WDS_IOCTL_GET_VERSION       CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_READ_ACCESS)
#define WDS_IOCTL_SEND_COMMAND      CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
#define WDS_IOCTL_READ_EVENTS       CTL_CODE(FILE_DEVICE_UNKNOWN, 0x803, METHOD_BUFFERED, FILE_READ_ACCESS)
#define WDS_IOCTL_ACKNOWLEDGE_EVENTS CTL_CODE(FILE_DEVICE_UNKNOWN, 0x804, METHOD_BUFFERED, FILE_WRITE_ACCESS)

// Vendor commands start at 0x100 (0x00-0xFF are reserved for the shim itself).
#define WDS_COMMAND_FIRST_VENDOR    0x100u

// Device interface GUID of the shim — applications/control planes open this.
// {D9C2A2E5-6F71-4E1B-9C3A-58B7F0D6E412}
DEFINE_GUID(WDS_SHIM_DEVICE_INTERFACE,
    0xD9C2A2E5, 0x6F71, 0x4E1B, 0x9C, 0x3A, 0x58, 0xB7, 0xF0, 0xD6, 0xE4, 0x12);

// One event record in the kernel ring buffer: fixed 16-byte header + DataSize payload bytes.
#pragma pack(push, 1)
typedef struct _WDS_EVENT_RECORD {
    UINT64 Sequence;
    UINT32 EventId;
    UINT32 DataSize;
    // UINT8 Data[DataSize];  — follows the header
} WDS_EVENT_RECORD, *PWDS_EVENT_RECORD;
C_ASSERT(sizeof(WDS_EVENT_RECORD) == 16);

typedef struct _WDS_COMMAND_HEADER {
    UINT32 CommandId;
    UINT32 DataSize;
    // UINT8 Data[DataSize];  — follows the header
} WDS_COMMAND_HEADER, *PWDS_COMMAND_HEADER;
C_ASSERT(sizeof(WDS_COMMAND_HEADER) == 8);

typedef struct _WDS_RESULT_HEADER {
    UINT32 Status;      // NTSTATUS of the command
    UINT32 DataSize;
    // UINT8 Data[DataSize];  — follows the header
} WDS_RESULT_HEADER, *PWDS_RESULT_HEADER;
C_ASSERT(sizeof(WDS_RESULT_HEADER) == 8);
#pragma pack(pop)

// Ring capacity: number of event records retained for user-mode readers.
#define WDS_EVENT_RING_CAPACITY 256
