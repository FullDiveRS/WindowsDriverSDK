using WindowsDriver.InfGen;

namespace WindowsDriver.InfGen;

/// <summary>
/// infgen — driver INF generator and linter.
///
///   infgen generate --manifest &lt;driver.json&gt; [--out &lt;file.inf&gt;] [--arch NTamd64,NTarm64]
///   infgen validate --inf &lt;file.inf&gt;
///   infgen schema
///
/// Exit codes: 0 = success, 1 = failure (message printed to stderr).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "generate":
                    return RunGenerate(args.Skip(1).ToArray());
                case "validate":
                    return RunValidate(args.Skip(1).ToArray());
                case "schema":
                    Console.WriteLine(ExampleManifest);
                    return 0;
                case "--help" or "-h" or "help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                    PrintUsage();
                    return 1;
            }
        }
        catch (InfGenException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"error: {ex.FileName ?? ex.Message} not found.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int RunGenerate(string[] args)
    {
        string? manifestPath = null;
        string? outPath = null;
        string[]? arches = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest" or "-m" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--out" or "-o" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--arch" when i + 1 < args.Length:
                    arches = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                default:
                    throw new InfGenException($"Unknown option '{args[i]}' for generate.");
            }
        }

        if (manifestPath is null)
        {
            throw new InfGenException("generate requires --manifest <driver.json>.");
        }

        var manifest = DriverManifest.FromJson(File.ReadAllText(manifestPath));
        var inf = new InfGenerator().Generate(manifest, arches);
        outPath ??= Path.ChangeExtension(manifestPath, ".inf");
        File.WriteAllText(outPath, inf);
        Console.WriteLine($"wrote {outPath} ({inf.Length} bytes, {manifest.Framework} driver '{manifest.Name}' v{manifest.Version})");
        return 0;
    }

    private static int RunValidate(string[] args)
    {
        string? infPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--inf" or "-f" when i + 1 < args.Length:
                    infPath = args[++i];
                    break;
                default:
                    throw new InfGenException($"Unknown option '{args[i]}' for validate.");
            }
        }
        if (infPath is null)
        {
            throw new InfGenException("validate requires --inf <file.inf>.");
        }

        var issues = new InfValidator().Validate(File.ReadAllText(infPath));
        if (issues.Count == 0)
        {
            Console.WriteLine($"{infPath}: OK (no issues found)");
            return 0;
        }
        foreach (var issue in issues)
        {
            Console.WriteLine($"{infPath}: [{issue.Section}] {issue.Message}");
        }
        Console.Error.WriteLine($"{infPath}: {issues.Count} issue(s) found.");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            infgen — Windows Driver SDK INF tool (v0.1.0-preview.1)

              infgen generate --manifest <driver.json> [--out <file.inf>] [--arch NTamd64,NTarm64]
                  Generates a driver INF from a JSON manifest.

              infgen validate --inf <file.inf>
                  Lints an INF: required sections, GUIDs, model refs, string refs.

              infgen schema
                  Prints an example manifest.

            """);
    }

    private const string ExampleManifest = """
        {
          // Windows Driver SDK manifest → driver INF
          "name": "MyDevice",
          "provider": "Contoso Devices",
          "manufacturer": "Contoso Devices LLC",
          "version": "1.0.0",
          "driverVerDate": "11/18/2026",
          "class": "Media",
          "classGuid": "{4d36e96c-e325-11ce-bfc1-08002be10318}",
          "framework": "umdf",
          "serviceName": "WdsMyDevice",
          "catalogFile": "MyDevice.cat",
          "files": [
            { "source": "MyDevice.dll", "destination": "drivers" },
            { "source": "MyDevice.json" }
          ],
          "models": [
            {
              "name": "Contoso Virtual MyDevice",
              "hardwareIds": ["Root\\ContosoMyDevice"],
              "compatibleIds": ["*WdsMyDevice"]
            }
          ],
          "wdfLibraryVersion": "2.15.0"
        }
        """;
}
