using System.Text.RegularExpressions;

namespace WindowsDriver.InfGen;

/// <summary>One lint finding for an INF file.</summary>
public sealed record InfIssue(string Section, string Message);

/// <summary>
/// Lightweight INF linter: verifies required sections exist, GUIDs parse, model
/// references resolve, and no section is duplicated. It does not replace the
/// full ChkINF / InfVerif tooling shipped with the WDK — run those before
/// submission (see docs/inf-and-signing.md).
/// </summary>
public sealed partial class InfValidator
{
    private static readonly string[] RequiredSections = ["Version", "Manufacturer", "Strings"];

    public IReadOnlyList<InfIssue> Validate(string infText)
    {
        var issues = new List<InfIssue>();
        var sections = ParseSections(infText);
        var sectionNames = sections.Keys.ToArray();

        foreach (var required in RequiredSections)
        {
            if (!sectionNames.Any(n => n.Equals(required, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new InfIssue(required, "required section is missing."));
            }
        }

        // Version block
        if (sections.TryGetValue("Version", out var version))
        {
            var keys = ParseKeys(version);
            foreach (var key in new[] { "Signature", "Class", "ClassGuid", "Provider", "DriverVer" })
            {
                if (!keys.ContainsKey(key))
                {
                    issues.Add(new InfIssue("Version", $"missing '{key}'."));
                }
            }
            if (keys.TryGetValue("ClassGuid", out var guid) && !Guid.TryParse(guid.Trim('{', '}'), out _))
            {
                issues.Add(new InfIssue("Version", $"ClassGuid '{guid}' is not a parseable GUID."));
            }
            if (keys.TryGetValue("DriverVer", out var ver) && !DriverVerRegex().IsMatch(ver))
            {
                issues.Add(new InfIssue("Version", $"DriverVer '{ver}' must be 'MM/DD/YYYY,version'."));
            }
        }

        // Duplicate sections
        var duplicates = sectionNames
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var duplicate in duplicates)
        {
            issues.Add(new InfIssue(duplicate, "section defined more than once."));
        }

        // Manufacturer model references
        foreach (var (name, lines) in sections.Where(kvp => kvp.Key.StartsWith("Standard.", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var line in lines.Where(l => l.TrimStart().StartsWith('%')))
            {
                // %ModelX%=Install.NTamd64,<hwids>
                var match = ModelLine().Match(line);
                if (!match.Success)
                {
                    issues.Add(new InfIssue(name, $"cannot parse model line '{line.Trim()}'."));
                    continue;
                }
                var installSection = match.Groups["install"].Value;
                if (!sections.ContainsKey(installSection))
                {
                    issues.Add(new InfIssue(name, $"model references missing install section [{installSection}]."));
                }
            }
        }

        // String references resolve (comments are stripped first: ';' starts a comment)
        var withoutComments = string.Join('\n', infText.Split(['\r', '\n'])
            .Select(line => line.Contains(';') ? line[..line.IndexOf(';')] : line));
        var strings = sections.TryGetValue("Strings", out var s) ? ParseKeys(s) : new Dictionary<string, string>();
        foreach (var reference in StringRefs().Matches(withoutComments).Select(m => m.Groups["key"].Value))
        {
            if (reference.All(char.IsDigit))
            {
                continue; // dirids like %12% are not string keys
            }
            if (!strings.ContainsKey(reference))
            {
                issues.Add(new InfIssue("Strings", $"string key '%{reference}%' is used but not defined."));
            }
        }

        return issues;
    }

    private static Dictionary<string, List<string>> ParseSections(string infText)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var raw in infText.Split(['\r', '\n']))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1].Trim();
                if (!result.ContainsKey(current))
                {
                    result[current] = [];
                }
                continue;
            }
            if (current is not null)
            {
                result[current].Add(line);
            }
        }
        return result;
    }

    private static Dictionary<string, string> ParseKeys(List<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        return result;
    }

    [GeneratedRegex(@"^(?<date>\d{2}/\d{2}/\d{4}),(?<version>.+)$")]
    private static partial Regex DriverVerRegex();

    [GeneratedRegex(@"^%(?<key>[^%]+)%\s*=\s*(?<install>[^,]+),")]
    private static partial Regex ModelLine();

    [GeneratedRegex(@"%(?<key>[A-Za-z0-9_.\-]+)%")]
    private static partial Regex StringRefs();
}
