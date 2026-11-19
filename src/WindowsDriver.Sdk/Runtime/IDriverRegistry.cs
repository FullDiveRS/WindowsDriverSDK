namespace WindowsDriver.Sdk;

/// <summary>
/// Read/write access to the driver's service Parameters key
/// (HKLM\SYSTEM\CurrentControlSet\Services\&lt;ServiceName&gt;\Parameters). The real
/// deployment is backed by Win32 registry APIs; tests use <see cref="MemoryRegistry"/>.
/// </summary>
public interface IDriverRegistry
{
    string? ReadString(string valueName);
    uint? ReadDword(string valueName);
    void WriteString(string valueName, string value);
    void WriteDword(string valueName, uint value);
    bool DeleteValue(string valueName);
}

/// <summary>In-memory <see cref="IDriverRegistry"/> used by the test host and as a default.</summary>
public class MemoryRegistry : IDriverRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (object Value, bool IsDword)> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? ReadString(string valueName)
    {
        lock (_gate)
        {
            return _values.TryGetValue(valueName, out var entry) && !entry.IsDword ? (string)entry.Value : null;
        }
    }

    public uint? ReadDword(string valueName)
    {
        lock (_gate)
        {
            return _values.TryGetValue(valueName, out var entry) && entry.IsDword ? (uint)entry.Value : null;
        }
    }

    public void WriteString(string valueName, string value)
    {
        lock (_gate)
        {
            _values[valueName] = (value, false);
        }
    }

    public void WriteDword(string valueName, uint value)
    {
        lock (_gate)
        {
            _values[valueName] = (value, true);
        }
    }

    public bool DeleteValue(string valueName)
    {
        lock (_gate)
        {
            return _values.Remove(valueName);
        }
    }
}
