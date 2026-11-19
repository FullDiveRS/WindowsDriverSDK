namespace WindowsDriver.Sdk;

/// <summary>
/// Device power states — values match the WDM DEVICE_POWER_STATE enum
/// (PowerDeviceUnspecified / D0 / D1 / D2 / D3).
/// </summary>
public enum DevicePowerState
{
    Unspecified = 0,
    D0 = 1,
    D1 = 2,
    D2 = 3,
    D3 = 4,
}
