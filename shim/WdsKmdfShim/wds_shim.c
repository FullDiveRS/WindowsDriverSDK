// wds_shim.c — KMDF shim driver skeleton for the Windows Driver SDK.
//
// ROLE
//   The shim is the kernel-side half of the SDK's KMDF interop story. It owns
//   the real KMDF device/queue objects (where C# cannot run) and exposes a
//   small buffered-IOCTL protocol that the managed control plane
//   (WindowsDriver.Interop.Kmdf.KmdfControlPlane) talks to from any .NET
//   process. Hot-path kernel work stays in C; everything else (policy, state
//   machines, telemetry, UI) lives in C#.
//
// STATUS
//   Skeleton: compiles against the WDK and implements the full IOCTL contract,
//   but the vendor command dispatch is a stub for you to fill in. Build it in
//   Visual Studio with the Windows Driver Kit (KMDF, Windows 10+, x64/arm64).
//   This file is NOT built by the SDK's CI — it is a reference implementation.
//
// BUILD
//   1. Visual Studio → New Project → "Kernel Mode Driver, KMDF" → name WdsKmdfShim.
//   2. Replace the generated driver.c/h with this file + wds_shim.h.
//   3. Add a pre-build step or project reference so wds_shim.h is on the include path.
//   4. Build, sign (see docs/inf-and-signing.md), install with tools/Install-Driver.ps1.

#include "wds_shim.h"

// ---------------------------------------------------------------------------
// Per-device context: the event ring and sequence counter.
typedef struct _WDS_DEVICE_CONTEXT {
    UINT8  Ring[WDS_EVENT_RING_CAPACITY][sizeof(WDS_EVENT_RECORD) + 256];
    UINT32 RingUsed[WDS_EVENT_RING_CAPACITY];
    UINT32 Head;        // next slot to write
    UINT64 NextSequence;
    WDFSPINLOCK RingLock;
} WDS_DEVICE_CONTEXT, *PWDS_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(WDS_DEVICE_CONTEXT, WdsDeviceGetContext)

// Forward declarations.
EVT_WDF_DRIVER_DEVICE_ADD WdsEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL WdsEvtIoDeviceControl;
EVT_WDF_OBJECT_CONTEXT_CLEANUP WdsEvtDriverContextCleanup;

static NTSTATUS WdsPostEvent(_In_ WDFDEVICE device, _In_ UINT32 eventId, _In_ UINT32 dataSize, _In_reads_bytes_opt_(dataSize) const UINT8* data);
static NTSTATUS WdsHandleCommand(_In_ WDFDEVICE device, _In_ const WDS_COMMAND_HEADER* command, _In_ UINT32 availableBytes, _Out_ UINT32* resultStatus, _Out_writes_bytes_(256) UINT8* resultData, _Out_ UINT32* resultSize);

// ---------------------------------------------------------------------------
// Driver entry — WDF does all the heavy lifting.
NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT driverObject, _In_ PUNICODE_STRING registryPath)
{
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_DRIVER_CONFIG config;

    WDF_DRIVER_CONFIG_INIT(&config, WdsEvtDeviceAdd);

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.EvtCleanupCallback = WdsEvtDriverContextCleanup;

    return WdfDriverCreate(driverObject, registryPath, &attributes, &config, WDF_NO_HANDLE);
}

// ---------------------------------------------------------------------------
// Device add: create the shim device, a sequential IO queue, and the interface.
NTSTATUS WdsEvtDeviceAdd(_In_ WDFDRIVER driver, _Inout_ PWDFDEVICE_INIT deviceInit)
{
    UNREFERENCED_PARAMETER(driver);

    NTSTATUS status;
    WDFDEVICE device = WDF_NO_HANDLE;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE queue;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, WDS_DEVICE_CONTEXT);

    status = WdfDeviceCreate(&deviceInit, &attributes, &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    // Initialize the event ring.
    PWDS_DEVICE_CONTEXT ctx = WdsDeviceGetContext(device);
    RtlZeroMemory(ctx, sizeof(*ctx));
    ctx->NextSequence = 1;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = device;
    status = WdfSpinLockCreate(&attributes, &ctx->RingLock);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    // Sequential queue: IOCTLs are processed one at a time (same semantics as
    // the SDK's QueueDispatchMode.Sequential).
    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = WdsEvtIoDeviceControl;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = device;

    status = WdfIoQueueCreate(device, &queueConfig, &attributes, &queue);
    if (!NT_SUCCESS(status))
    {
        return status;
    }
    status = WdfDeviceConfigureRequestDispatching(device, queue, WdfRequestTypeDeviceControl);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    // Expose the interface the managed control plane opens.
    status = WdfDeviceCreateDeviceInterface(device, &WDS_SHIM_DEVICE_INTERFACE, NULL);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    // Boot event: the control plane sees this as soon as it drains the ring.
    return WdsPostEvent(device, 0x0001 /* WDS_EVENT_DEVICE_STARTED */, 0, NULL);
}

// ---------------------------------------------------------------------------
// IOCTL dispatch — the whole managed/kernel contract lives here.
VOID WdsEvtIoDeviceControl(_In_ WDFQUEUE queue, _In_ WDFREQUEST request, _In_ size_t outputBufferLength,
                           _In_ size_t inputBufferLength, _In_ ULONG ioControlCode)
{
    WDFDEVICE device = WdfIoQueueGetDevice(queue);
    PWDS_DEVICE_CONTEXT ctx = WdsDeviceGetContext(device);
    NTSTATUS status = STATUS_SUCCESS;
    PVOID input = NULL;
    PVOID output = NULL;
    size_t actualInput = 0;
    size_t actualOutput = 0;
    ULONG information = 0;

    switch (ioControlCode)
    {
    case WDS_IOCTL_PING:
    {
        if (outputBufferLength < sizeof(UINT32))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        status = WdfRequestRetrieveOutputBuffer(request, sizeof(UINT32), &output, &actualOutput);
        if (!NT_SUCCESS(status))
        {
            break;
        }
        *(UINT32*)output = WDS_SHIM_MAGIC;
        information = sizeof(UINT32);
        break;
    }

    case WDS_IOCTL_GET_VERSION:
    {
        if (outputBufferLength < 2 * sizeof(UINT32))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        status = WdfRequestRetrieveOutputBuffer(request, 2 * sizeof(UINT32), &output, &actualOutput);
        if (!NT_SUCCESS(status))
        {
            break;
        }
        ((UINT32*)output)[0] = WDS_SHIM_VERSION_MAJOR;
        ((UINT32*)output)[1] = WDS_SHIM_VERSION_MINOR;
        information = 2 * sizeof(UINT32);
        break;
    }

    case WDS_IOCTL_SEND_COMMAND:
    {
        if (inputBufferLength < sizeof(WDS_COMMAND_HEADER) || outputBufferLength < sizeof(WDS_RESULT_HEADER))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        status = WdfRequestRetrieveInputBuffer(request, sizeof(WDS_COMMAND_HEADER), &input, &actualInput);
        if (!NT_SUCCESS(status))
        {
            break;
        }
        status = WdfRequestRetrieveOutputBuffer(request, sizeof(WDS_RESULT_HEADER), &output, &actualOutput);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        const WDS_COMMAND_HEADER* command = (const WDS_COMMAND_HEADER*)input;
        if (command->DataSize > actualInput - sizeof(WDS_COMMAND_HEADER))
        {
            status = STATUS_INVALID_PARAMETER;
            break;
        }

        UINT32 resultSize = 0;
        UINT32 resultStatus = STATUS_SUCCESS;
        UINT8* resultData = ((UINT8*)output) + sizeof(WDS_RESULT_HEADER);
        UINT32 resultCapacity = (UINT32)(actualOutput - sizeof(WDS_RESULT_HEADER));
        if (resultCapacity > 256)
        {
            resultCapacity = 256;
        }

        status = WdsHandleCommand(device, command, command->DataSize, &resultStatus, resultData, &resultSize);

        PWDS_RESULT_HEADER result = (PWDS_RESULT_HEADER)output;
        result->Status = resultStatus;
        result->DataSize = resultSize;
        information = sizeof(WDS_RESULT_HEADER) + resultSize;
        break;
    }

    case WDS_IOCTL_READ_EVENTS:
    {
        // Drain up to N events into the output buffer: (u32 count) + records.
        status = WdfRequestRetrieveOutputBuffer(request, sizeof(UINT32), &output, &actualOutput);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        UINT32* countOut = (UINT32*)output;
        UINT8* cursor = ((UINT8*)output) + sizeof(UINT32);
        UINT8* end = ((UINT8*)output) + actualOutput;
        UINT32 drained = 0;

        WdfSpinLockAcquire(ctx->RingLock);
        for (UINT32 i = 0; i < WDS_EVENT_RING_CAPACITY; i++)
        {
            UINT32 used = ctx->RingUsed[(ctx->Head + i) % WDS_EVENT_RING_CAPACITY];
            if (used == 0)
            {
                continue;
            }
            UINT32 total = (UINT32)(sizeof(WDS_EVENT_RECORD) + used);
            if ((size_t)(end - cursor) < sizeof(WDS_EVENT_RECORD) + used)
            {
                break; // output buffer full
            }
            WDS_EVENT_RECORD* record = (WDS_EVENT_RECORD*)cursor;
            const UINT8* slot = ctx->Ring[(ctx->Head + i) % WDS_EVENT_RING_CAPACITY];
            RtlCopyMemory(record, slot, total);
            cursor += total;
            drained++;
        }
        // Consumed events are removed by ACKNOWLEDGE_EVENTS; here they stay so
        // a crash does not lose telemetry. For the skeleton, drain-and-keep.
        WdfSpinLockRelease(ctx->RingLock);

        *countOut = drained;
        information = (ULONG)(sizeof(UINT32) + (cursor - ((UINT8*)output) - sizeof(UINT32)));
        break;
    }

    case WDS_IOCTL_ACKNOWLEDGE_EVENTS:
        // Skeleton: clearing the ring is left to the integrator (drop consumed
        // records below the acknowledged sequence).
        status = STATUS_SUCCESS;
        break;

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    WdfRequestCompleteWithInformation(request, status, information);
}

// ---------------------------------------------------------------------------
// Vendor command dispatch — replace the stub with your kernel-side work.
NTSTATUS WdsHandleCommand(_In_ WDFDEVICE device, _In_ const WDS_COMMAND_HEADER* command,
                          _In_ UINT32 availableBytes, _Out_ UINT32* resultStatus,
                          _Out_writes_bytes_(256) UINT8* resultData, _Out_ UINT32* resultSize)
{
    UNREFERENCED_PARAMETER(device);
    UNREFERENCED_PARAMETER(availableBytes);
    UNREFERENCED_PARAMETER(resultData);

    *resultSize = 0;

    if (command->CommandId < WDS_COMMAND_FIRST_VENDOR)
    {
        *resultStatus = STATUS_INVALID_DEVICE_REQUEST;
        return STATUS_SUCCESS; // the IOCTL itself succeeded; the command failed
    }

    // TODO: vendor commands — read command->Data (command->DataSize bytes),
    // perform the kernel operation, write results into resultData, post events
    // with WdsPostEvent for asynchronous completions.
    *resultStatus = STATUS_NOT_IMPLEMENTED;
    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------
// Ring post helper.
NTSTATUS WdsPostEvent(_In_ WDFDEVICE device, _In_ UINT32 eventId, _In_ UINT32 dataSize,
                      _In_reads_bytes_opt_(dataSize) const UINT8* data)
{
    PWDS_DEVICE_CONTEXT ctx = WdsDeviceGetContext(device);
    if (dataSize > 256)
    {
        return STATUS_INVALID_PARAMETER;
    }

    NTSTATUS status = STATUS_SUCCESS;
    WdfSpinLockAcquire(ctx->RingLock);
    UINT32 slot = ctx->Head % WDS_EVENT_RING_CAPACITY;
    WDS_EVENT_RECORD* record = (WDS_EVENT_RECORD*)ctx->Ring[slot];
    record->Sequence = ctx->NextSequence++;
    record->EventId = eventId;
    record->DataSize = dataSize;
    if (data != NULL && dataSize > 0)
    {
        RtlCopyMemory((UINT8*)record + sizeof(WDS_EVENT_RECORD), data, dataSize);
    }
    ctx->RingUsed[slot] = dataSize;
    ctx->Head = (ctx->Head + 1) % WDS_EVENT_RING_CAPACITY;
    WdfSpinLockRelease(ctx->RingLock);
    return status;
}

VOID WdsEvtDriverContextCleanup(_In_ WDFOBJECT driverObject)
{
    UNREFERENCED_PARAMETER(driverObject);
}
