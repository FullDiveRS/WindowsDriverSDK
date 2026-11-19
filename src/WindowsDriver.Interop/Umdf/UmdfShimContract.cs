namespace WindowsDriver.Interop.Umdf;

/// <summary>
/// The exact C declarations the native UMDF shim exposes (exports of the shim
/// DLL, e.g. WdsShimU.dll) and the managed function pointers it resolves via
/// hostfxr. Keep this in sync with shim/ and docs/umdf-guide.md.
/// </summary>
public static class UmdfShimContract
{
    /// <summary>Exported function names of the native shim.</summary>
    public static class ShimExports
    {
        public const string DllGetClassObject = "DllGetClassObject";     // standard COM entry the WUDFHost uses
        public const string DriverEntry = "DriverEntry";                 // UMDF module entry
    }

    /// <summary>Managed entry points (resolved via hostfxr load_assembly_and_get_function_pointer).</summary>
    public static class ManagedEntryPoints
    {
        public const string Initialize = "WdsInitialize";
        public const string DeviceAdd = "WdsDeviceAdd";
        public const string QueueRequest = "WdsQueueRequest";
        public const string DeviceD0Entry = "WdsDeviceD0Entry";
        public const string DeviceD0Exit = "WdsDeviceD0Exit";
        public const string DeviceRelease = "WdsDeviceRelease";
        public const string DriverUnload = "WdsDriverUnload";
    }

    /// <summary>
    /// C header the shim compiles against (mirror of the managed struct + entry
    /// points above). Included here as the single source of truth for the ABI.
    /// </summary>
    public const string ShimHeader = """
// wds_shim_abi.h — ABI between the native UMDF shim and the managed driver host.
// Include from the shim project; the managed side mirrors it in UmdfShimSettings.
#pragma once
#include <windows.h>

typedef int  (*PFN_WdsCompleteRequest)(unsigned long long requestId, int ntStatus, int bytesTransferred);
typedef void (*PFN_WdsLog)(int level, const unsigned char* utf8Message, int length);
typedef void (*PFN_WdsArmQueue)(int queueIndex);

typedef struct WDS_SHIM_SETTINGS {
    PFN_WdsCompleteRequest CompleteRequest;
    PFN_WdsLog             Log;
    PFN_WdsArmQueue        ArmQueue;
    const unsigned char*   DriverServiceNameUtf8;  // null-terminated
    const unsigned char*   DriverTypeNameUtf8;     // null-terminated
} WDS_SHIM_SETTINGS;

// Managed entry points, resolved with hostfxr's
// load_assembly_and_get_function_pointer:
typedef int (*PFN_WdsInitialize)(WDS_SHIM_SETTINGS* settings);
typedef int (*PFN_WdsDeviceAdd)(const wchar_t* deviceInstanceId,
                                const wchar_t** hardwareIds,
                                int hardwareIdCount);
typedef int (*PFN_WdsQueueRequest)(unsigned long long requestId,
                                   int kind,              // 0=DeviceControl, 1=Read, 2=Write
                                   unsigned long ioControlCode,
                                   const unsigned char* input,  int inputLength,
                                   unsigned char* output,       int outputLength);
typedef int (*PFN_WdsDeviceD0Entry)(int previousState);
typedef int (*PFN_WdsDeviceD0Exit)(int newState);
typedef int (*PFN_WdsDeviceRelease)(void);
typedef int (*PFN_WdsDriverUnload)(void);
""";
}
