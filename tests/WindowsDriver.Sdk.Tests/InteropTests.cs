using System.Runtime.InteropServices;
using WindowsDriver.Interop.Kmdf;
using WindowsDriver.Interop.Umdf;
using WindowsDriver.Sdk;
using Xunit;

namespace WindowsDriver.Sdk.Tests;

public class InteropTests
{
    // ----- KMDF shim protocol -----

    [Fact]
    public void Shim_ioctl_codes_are_unique_and_vendor_scoped()
    {
        var codes = new[]
        {
            KmdfShimProtocol.Ping,
            KmdfShimProtocol.GetVersion,
            KmdfShimProtocol.SendCommand,
            KmdfShimProtocol.ReadEvents,
            KmdfShimProtocol.AcknowledgeEvents,
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, c =>
        {
            Assert.True(WinIoctl.FunctionFromControl(c) >= WinIoctl.FirstVendorFunction, $"0x{c:X8} must use a vendor function code");
            Assert.Equal(WinIoctl.MethodBuffered, WinIoctl.MethodFromControl(c));
        });
    }

    [Fact]
    public void Wire_struct_sizes_are_stable()
    {
        Assert.Equal(8, KmdfCommandHeader.WireSize);
        Assert.Equal(8, KmdfResultHeader.WireSize);
        Assert.Equal(16, KmdfEventRecord.WireSize);
        Assert.Equal(8, Marshal.SizeOf<KmdfCommandHeader>());
        Assert.Equal(16, Marshal.SizeOf<KmdfEventRecord>());
        Assert.Equal(16, KmdfEventRecord.TotalSize(0));
        Assert.Equal(17, KmdfEventRecord.TotalSize(1));
    }

    [Fact]
    public void Magic_reads_as_ascii()
    {
        var bytes = BitConverter.GetBytes(KmdfShimProtocol.Magic);
        Assert.Equal("WSHI", System.Text.Encoding.ASCII.GetString(bytes));
    }

    // ----- UMDF shim contract -----

    [Fact]
    public void Shim_header_declares_the_managed_entry_points()
    {
        var header = UmdfShimContract.ShimHeader;
        Assert.Contains("PFN_WdsInitialize", header);
        Assert.Contains("PFN_WdsQueueRequest", header);
        Assert.Contains("WDS_SHIM_SETTINGS", header);

        Assert.Equal("WdsInitialize", UmdfShimContract.ManagedEntryPoints.Initialize);
        Assert.Equal("WdsQueueRequest", UmdfShimContract.ManagedEntryPoints.QueueRequest);
    }

    [Fact]
    public void Umdf_settings_struct_matches_wire_size()
    {
        // 5 pointers on 64-bit.
        Assert.Equal(5 * IntPtr.Size, Marshal.SizeOf<UmdfShimSettings>());
        Assert.Equal(UmdfShimSettings.WireSize, Marshal.SizeOf<UmdfShimSettings>());
    }

    // ----- KMDF control plane (no device: error paths) -----

    [Fact]
    public void Control_plane_requires_open_before_use()
    {
        var plane = new KmdfControlPlane(@"\\.\WdsKmdfShim0");
        Assert.Throws<InvalidOperationException>(() => plane.Ping());
        Assert.Throws<InvalidOperationException>(() => plane.ReadEvents());
    }
}
