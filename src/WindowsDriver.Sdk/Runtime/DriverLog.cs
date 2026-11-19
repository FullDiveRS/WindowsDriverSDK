using System.Globalization;
using System.Text;

namespace WindowsDriver.Sdk;

/// <summary>Log levels a driver can emit (maps onto ETW verbosity levels).</summary>
public enum DriverLogLevel
{
    Trace = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Logging facade for drivers. In a real deployment this is backed by ETW (or
/// WPP-style tracing); in the test host it is backed by <c>CapturedLog</c>.
/// </summary>
public interface IDriverLog
{
    bool IsEnabled(DriverLogLevel level);

    /// <summary>Writes a pre-formatted message. Use the extension methods for formatting.</summary>
    void Log(DriverLogLevel level, string message);
}

public static class DriverLogExtensions
{
    public static void Trace(this IDriverLog log, string format, params object?[] args) => log.WriteAt(DriverLogLevel.Trace, format, args);
    public static void Info(this IDriverLog log, string format, params object?[] args) => log.WriteAt(DriverLogLevel.Info, format, args);
    public static void Warning(this IDriverLog log, string format, params object?[] args) => log.WriteAt(DriverLogLevel.Warning, format, args);
    public static void Error(this IDriverLog log, string format, params object?[] args) => log.WriteAt(DriverLogLevel.Error, format, args);

    private static void WriteAt(this IDriverLog log, DriverLogLevel level, string format, object?[] args)
    {
        if (!log.IsEnabled(level))
        {
            return;
        }
        log.Log(level, args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args));
    }
}

/// <summary>A logger that discards everything.</summary>
public sealed class NullDriverLog : IDriverLog
{
    public static readonly NullDriverLog Instance = new();
    public bool IsEnabled(DriverLogLevel level) => false;
    public void Log(DriverLogLevel level, string message) { }
}

/// <summary>Writes formatted lines to the console: "12:00:00.000 [Info] message".</summary>
public sealed class ConsoleDriverLog : IDriverLog
{
    public static readonly ConsoleDriverLog Instance = new();

    private readonly DriverLogLevel _minimum;

    public ConsoleDriverLog(DriverLogLevel minimum = DriverLogLevel.Trace) => _minimum = minimum;

    public bool IsEnabled(DriverLogLevel level) => level >= _minimum;

    public void Log(DriverLogLevel level, string message)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()}] {message}";
        Console.WriteLine(line);
    }
}

/// <summary>Thread-safe file logger (UTF-8, append or overwrite).</summary>
public sealed class FileDriverLog : IDriverLog, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly DriverLogLevel _minimum;

    public FileDriverLog(string path, bool append = false, DriverLogLevel minimum = DriverLogLevel.Trace)
    {
        _minimum = minimum;
        _writer = new StreamWriter(new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);
    }

    public bool IsEnabled(DriverLogLevel level) => level >= _minimum;

    public void Log(DriverLogLevel level, string message)
    {
        lock (_gate)
        {
            _writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()}] {message}");
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}

/// <summary>Fans messages out to multiple sinks.</summary>
public sealed class CompositeDriverLog(params IDriverLog[] sinks) : IDriverLog
{
    public bool IsEnabled(DriverLogLevel level) => sinks.Any(s => s.IsEnabled(level));
    public void Log(DriverLogLevel level, string message)
    {
        foreach (var sink in sinks)
        {
            if (sink.IsEnabled(level))
            {
                sink.Log(level, message);
            }
        }
    }
}

/// <summary>Prefixes every message with a scope tag, e.g. the device instance id.</summary>
public sealed class ScopedDriverLog(IDriverLog inner, string scope) : IDriverLog
{
    public bool IsEnabled(DriverLogLevel level) => inner.IsEnabled(level);
    public void Log(DriverLogLevel level, string message) => inner.Log(level, $"[{scope}] {message}");
}
