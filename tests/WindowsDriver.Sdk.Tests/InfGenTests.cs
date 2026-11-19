using WindowsDriver.InfGen;
using Xunit;

namespace WindowsDriver.Sdk.Tests;

public class InfGenTests
{
    private static DriverManifest SampleManifest => new()
    {
        Name = "TestDevice",
        Provider = "Contoso Devices",
        Version = "1.2.3",
        Class = "Media",
        ClassGuid = "{4d36e96c-e325-11ce-bfc1-08002be10318}",
        Framework = DriverFramework.Umdf,
        Files = [new DriverFile("TestDevice.dll")],
        Models = [new DriverModel { Name = "Contoso Virtual Test Device", HardwareIds = ["Root\\ContosoTest"] }],
    };

    [Fact]
    public void Generator_produces_all_required_grammar_blocks()
    {
        var inf = new InfGenerator(new DateOnly(2026, 11, 18)).Generate(SampleManifest);

        Assert.Contains("Signature=\"$WINDOWS NT$\"", inf);
        Assert.Contains("Class=Media", inf);
        Assert.Contains("ClassGuid={4d36e96c-e325-11ce-bfc1-08002be10318}", inf);
        Assert.Contains("CatalogFile=TestDevice.cat", inf);
        Assert.Contains("DriverVer=11/18/2026,1.2.3", inf);
        Assert.Contains("PnpLockdown=1", inf);
        Assert.Contains("[Manufacturer]", inf);
        Assert.Contains("%Manufacturer%=Standard,NTamd64,NTarm64", inf);
        Assert.Contains("[Standard.NTamd64]", inf);
        Assert.Contains("%Model0%=Install.NTamd64,Root\\ContosoTest", inf);
        Assert.Contains("[Install.NTamd64.Wdf]", inf);
        Assert.Contains("UmdfService=TestDevice, TestDevice_Install", inf);
        Assert.Contains("UmdfLibraryVersion=2.15.0", inf);
        Assert.Contains("ServiceBinary=%12%\\TestDevice.dll", inf);
        Assert.Contains("DriverFiles = 12", inf);
        Assert.Contains("Model0 = \"Contoso Virtual Test Device\"", inf);
    }

    [Fact]
    public void Generator_switches_to_kmdf_sections()
    {
        var manifest = SampleManifest with { Framework = DriverFramework.Kmdf };
        var inf = new InfGenerator(new DateOnly(2026, 11, 18)).Generate(manifest);

        Assert.Contains("KmdfService=TestDevice, TestDevice_Install", inf);
        Assert.Contains("KmdfLibraryVersion=1.15", inf);
        Assert.DoesNotContain("UmdfService", inf);
    }

    [Fact]
    public void Generated_inf_passes_the_validator()
    {
        var inf = new InfGenerator(new DateOnly(2026, 11, 18)).Generate(SampleManifest);
        var issues = new InfValidator().Validate(inf);
        Assert.True(issues.Count == 0, string.Join("; ", issues.Select(i => $"[{i.Section}] {i.Message}")));
    }

    [Fact]
    public void Validator_flags_broken_infs()
    {
        var broken = """
            [Version]
            Signature="$WINDOWS NT$"
            ClassGuid={not-a-guid}
            DriverVer=2026.11.18,1.0

            [Strings]
            Unused = "x"

            [Standard.NTamd64]
            %Model0%=Install,Root\Broken
            """;
        var issues = new InfValidator().Validate(broken);
        Assert.Contains(issues, i => i.Section == "Version" && i.Message.Contains("Class"));
        Assert.Contains(issues, i => i.Section == "Version" && i.Message.Contains("Provider"));
        Assert.Contains(issues, i => i.Section == "Version" && i.Message.Contains("DriverVer"));
        Assert.Contains(issues, i => i.Section == "Version" && i.Message.Contains("ClassGuid"));
        Assert.Contains(issues, i => i.Section == "Manufacturer" && i.Message.Contains("missing"));
        Assert.Contains(issues, i => i.Section == "Standard.NTamd64" && i.Message.Contains("missing install section"));
        Assert.Contains(issues, i => i.Section == "Strings" && i.Message.Contains("Model0"));
    }

    [Fact]
    public void Manifest_validation_catches_missing_fields()
    {
        // Missing required JSON properties are reported by the deserializer.
        var problems = Assert.Throws<InfGenException>(() => DriverManifest.FromJson("""
            { "name": "", "classGuid": "nope", "framework": "umdf" }
            """));
        Assert.Contains("provider", problems.Message);
        Assert.Contains("version", problems.Message);

        // Present but invalid values are caught by manifest validation.
        var invalid = Assert.Throws<InfGenException>(() => DriverManifest.FromJson("""
            {
              "name": "X",
              "provider": "P",
              "version": "1",
              "class": "Media",
              "classGuid": "nope",
              "framework": "umdf",
              "files": [{ "source": "x.sys" }],
              "models": [{ "name": "m", "hardwareIds": ["Root\\X"] }]
            }
            """));
        Assert.Contains("classGuid", invalid.Message);
        Assert.Contains("parseable GUID", invalid.Message);
    }

    [Fact]
    public void Manifest_parses_camel_case_json()
    {
        var manifest = DriverManifest.FromJson("""
            {
              "name": "P",
              "provider": "Contoso",
              "version": "0.1",
              "class": "Ports",
              "classGuid": "{4d36e978-e325-11ce-bfc1-08002be10318}",
              "framework": "kmdf",
              "files": [{ "source": "p.sys" }],
              "models": [{ "name": "P device", "hardwareIds": ["Root\\P"] }]
            }
            """);
        Assert.Equal(DriverFramework.Kmdf, manifest.Framework);
        Assert.Equal("Ports", manifest.Class);
    }

    [Fact]
    public void Guid_normalization_adds_braces_and_lowercases()
    {
        Assert.Equal("{4d36e96c-e325-11ce-bfc1-08002be10318}",
            InfGenerator.NormalizeGuid("4D36E96C-E325-11CE-BFC1-08002BE10318"));
    }
}
