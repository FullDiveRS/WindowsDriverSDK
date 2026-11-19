using WindowsDriver.Sdk;

namespace WindowsDriver.Testing;

/// <summary>An in-memory registry with seed values (wraps the core MemoryRegistry).</summary>
public sealed class FakeRegistry : MemoryRegistry
{
    public FakeRegistry(params (string Key, object Value)[] seed)
    {
        foreach (var (key, value) in seed)
        {
            switch (value)
            {
                case string s: WriteString(key, s); break;
                case uint u: WriteDword(key, u); break;
                case int i when i >= 0: WriteDword(key, (uint)i); break;
                case bool b: WriteDword(key, b ? 1u : 0u); break;
                default:
                    throw new ArgumentException($"Unsupported seed value type {value.GetType().Name} for '{key}'.");
            }
        }
    }
}

/// <summary>Records every log entry for assertions.</summary>
public sealed class CapturedLog(DriverLogLevel minimum = DriverLogLevel.Trace) : IDriverLog
{
    private readonly object _gate = new();
    private readonly List<(DriverLogLevel Level, string Message, DateTimeOffset At)> _entries = [];

    public bool IsEnabled(DriverLogLevel level) => level >= minimum;

    public void Log(DriverLogLevel level, string message)
    {
        lock (_gate)
        {
            _entries.Add((level, message, DateTimeOffset.UtcNow));
        }
    }

    public IReadOnlyList<string> Messages
    {
        get { lock (_gate) { return _entries.Select(e => e.Message).ToArray(); } }
    }

    public IReadOnlyList<(DriverLogLevel Level, string Message, DateTimeOffset At)> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    public bool Contains(string substring)
    {
        lock (_gate)
        {
            return _entries.Any(e => e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool ContainsError() => Entries.Any(e => e.Level == DriverLogLevel.Error);

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}

/// <summary>Framework-agnostic assertion helpers for driver tests.</summary>
public static class Check
{
    /// <summary>Throws with a detailed message when the result status differs.</summary>
    public static void Status(IoResult result, NtStatus expected, string? because = null)
    {
        if (result.Status != expected)
        {
            throw new TestCheckException(
                $"{because}: expected status {expected.ToHexString()} but got {result.Status.ToHexString()} " +
                $"({result.BytesTransferred} bytes).");
        }
    }

    public static void Success(IoResult result, string? because = null) => Status(result, NtStatus.Success, because);

    public static void Bytes(IoResult result, int expectedBytes, string? because = null)
    {
        if (result.BytesTransferred != expectedBytes)
        {
            throw new TestCheckException($"{because}: expected {expectedBytes} bytes, got {result.BytesTransferred}.");
        }
    }
}

/// <summary>Thrown by <see cref="Check"/> helpers — any test framework treats it as a failure.</summary>
public sealed class TestCheckException(string message) : Exception(message);

/// <summary>Small async polling helper for deterministic waits (real-time, used only where fake-time callbacks cross threads).</summary>
public static class TestSync
{
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? what = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met within {(timeout ?? TimeSpan.FromSeconds(5)).TotalSeconds}s: {what ?? "unnamed"}.");
            }
            await Task.Delay(10);
        }
    }
}
