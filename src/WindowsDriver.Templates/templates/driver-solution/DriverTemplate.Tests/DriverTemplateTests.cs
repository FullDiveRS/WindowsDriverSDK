using WindowsDriver.Sdk;
using WindowsDriver.Testing;
using Xunit;

namespace DriverTemplate.Tests;

public class DriverTemplateTests
{
    [Fact]
    public async Task Device_boots_and_rejects_unknown_ioctls()
    {
        var host = TestDriverHost.Builder().UseFakeTime().Build();
        await host.StartAsync<DriverTemplateDriver>("DriverTemplateService");
        var device = await host.AddDeviceAsync("ROOT\\DRIVERTEMPLATE\\0000", "Root\\DriverTemplate");

        Assert.True(device.Device.IsD0, "device should be powered after AddDevice");

        var unknown = await device.SendIoctlAsync(0xDEAD);
        Check.Status(unknown, NtStatus.InvalidDeviceRequest);

        await host.ShutdownAsync();
    }
}
