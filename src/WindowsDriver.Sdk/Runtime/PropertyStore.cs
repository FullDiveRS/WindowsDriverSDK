using System.Globalization;

namespace WindowsDriver.Sdk;

/// <summary>
/// A typed, string-keyed store for device and driver properties — the managed
/// stand-in for DEVPKEY properties and registry blobs attached to a devnode.
/// </summary>
public sealed class PropertyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Keys
    {
        get { lock (_gate) { return _values.Keys.ToArray(); } }
    }

    public int Count
    {
        get { lock (_gate) { return _values.Count; } }
    }

    public bool Contains(string key)
    {
        lock (_gate)
        {
            return _values.ContainsKey(key);
        }
    }

    public string? GetString(string key, string? defaultValue = null)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) && value is string s ? s : defaultValue;
        }
    }

    public uint? GetDword(string key, uint? defaultValue = null)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value)
                ? value switch
                {
                    uint u => u,
                    int i when i >= 0 => (uint)i,
                    string s when uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => defaultValue,
                }
                : defaultValue;
        }
    }

    public long? GetLong(string key, long? defaultValue = null)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value)
                ? value switch
                {
                    long l => l,
                    int i => i,
                    uint u => u,
                    _ => defaultValue,
                }
                : defaultValue;
        }
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value)
                ? value switch
                {
                    bool b => b,
                    uint u => u != 0,
                    string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
                    _ => defaultValue,
                }
                : defaultValue;
        }
    }

    public Guid? GetGuid(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value)
                ? value switch
                {
                    Guid g => g,
                    string s when Guid.TryParse(s, out var parsed) => parsed,
                    _ => null,
                }
                : null;
        }
    }

    public object? GetRaw(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Set(string key, string value) => SetRaw(key, value);
    public void Set(string key, uint value) => SetRaw(key, value);
    public void Set(string key, int value) => SetRaw(key, value);
    public void Set(string key, long value) => SetRaw(key, value);
    public void Set(string key, bool value) => SetRaw(key, value);
    public void Set(string key, Guid value) => SetRaw(key, value);

    public void SetRaw(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            _values[key] = value;
        }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            return _values.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return $"PropertyStore[{_values.Count}]: " + string.Join(", ", _values.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        }
    }
}

/// <summary>Well-known property key names used by samples and tooling.</summary>
public static class KnownDeviceProperties
{
    public const string FriendlyName = "Device_FriendlyName";
    public const string Manufacturer = "Device_Manufacturer";
    public const string DeviceDescription = "Device_DeviceDesc";
    public const string DriverVersion = "Device_DriverVersion";
    public const string InterfaceClassGuid = "Device_InterfaceClassGuid";
}
