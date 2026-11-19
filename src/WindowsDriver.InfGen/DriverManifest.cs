using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsDriver.InfGen;

/// <summary>WDF flavor the driver package targets.</summary>
public enum DriverFramework
{
    Umdf,
    Kmdf,
}

/// <summary>One driver file to copy during install.</summary>
public sealed record DriverFile(string Source, string? Destination = null);

/// <summary>One hardware model supported by the package.</summary>
public sealed record DriverModel
{
    /// <summary>Display name (the %Model% string).</summary>
    public required string Name { get; init; }

    /// <summary>Hardware IDs, most specific first.</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    /// <summary>Optional compatible IDs.</summary>
    public IReadOnlyList<string> CompatibleIds { get; init; } = [];
}

/// <summary>
/// The manifest InfGen turns into a driver INF. All fields are required except
/// where noted; see docs/inf-and-signing.md for the full schema reference.
/// </summary>
public sealed record DriverManifest
{
    /// <summary>Base name for the INF, catalog and service (no extension).</summary>
    public required string Name { get; init; }

    /// <summary>Publishing provider (the %Provider% string).</summary>
    public required string Provider { get; init; }

    /// <summary>Manufacturer display name; defaults to Provider.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Driver version, e.g. "0.1.0".</summary>
    public required string Version { get; init; }

    /// <summary>DriverVer date (MM/DD/YYYY). Defaults to the generation date.</summary>
    public string? DriverVerDate { get; init; }

    public required string Class { get; init; }

    /// <summary>Setup class GUID in {xxxxxxxx-...} form (with braces).</summary>
    public required string ClassGuid { get; init; }

    public required DriverFramework Framework { get; init; }

    /// <summary>Service name for the WDF section; defaults to Name.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Catalog file name; defaults to &lt;Name&gt;.cat.</summary>
    public string? CatalogFile { get; init; }

    /// <summary>Files copied by the driver package. The first entry must be the driver binary.</summary>
    public IReadOnlyList<DriverFile> Files { get; init; } = [];

    public IReadOnlyList<DriverModel> Models { get; init; } = [];

    /// <summary>WDF library versions: "2.15.0" for UMDF, "1.15" for KMDF.</summary>
    public string? WdfLibraryVersion { get; init; }

    public static DriverManifest FromJson(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        DriverManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DriverManifest>(json, options)
                       ?? throw new InfGenException("Manifest parsed to null — is the JSON valid?");
        }
        catch (JsonException ex)
        {
            throw new InfGenException($"Manifest JSON is invalid: {ex.Message}");
        }
        Validate(manifest);
        return manifest;
    }

    public static void Validate(DriverManifest manifest)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            problems.Add("name is required.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Provider))
        {
            problems.Add("provider is required.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            problems.Add("version is required.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Class))
        {
            problems.Add("class is required.");
        }
        if (string.IsNullOrWhiteSpace(manifest.ClassGuid) || !Guid.TryParse(manifest.ClassGuid.Trim('{', '}'), out _))
        {
            problems.Add($"classGuid '{manifest.ClassGuid}' is not a parseable GUID.");
        }
        if (manifest.Files.Count == 0)
        {
            problems.Add("files must contain at least the driver binary.");
        }
        if (manifest.Models.Count == 0)
        {
            problems.Add("at least one model is required.");
        }
        foreach (var model in manifest.Models)
        {
            if (model.HardwareIds.Count == 0 && model.CompatibleIds.Count == 0)
            {
                problems.Add($"model '{model.Name}' needs at least one hardware ID or compatible ID.");
            }
        }

        if (problems.Count > 0)
        {
            throw new InfGenException("Manifest invalid:\n  - " + string.Join("\n  - ", problems));
        }
    }
}

/// <summary>Tool error with a friendly message (exit code 1).</summary>
public sealed class InfGenException(string message) : Exception(message);
