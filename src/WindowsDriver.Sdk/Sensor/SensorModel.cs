namespace WindowsDriver.Sdk.Sensor;

/// <summary>Categories of sensors the SDK models (map onto Windows sensor categories/types when integrating with SensorCx).</summary>
public enum SensorKind
{
    Accelerometer3D,
    AmbientLight,
    Custom,
}

/// <summary>A single sampled reading from a sensor.</summary>
public sealed record SensorReading(
    SensorKind Kind,
    DateTimeOffset Timestamp,
    IReadOnlyList<double> Values,
    string Unit);

/// <summary>
/// A sampled data source for <see cref="SensorDevice"/>. Implementations are
/// pure functions of the timestamp, so tests are fully deterministic.
/// </summary>
public interface ISensorSource
{
    SensorKind Kind { get; }
    string Unit { get; }
    int ValueCount { get; }

    /// <summary>Samples-per-second the device should poll at.</summary>
    double SampleRateHz { get; set; }

    SensorReading Sample(DateTimeOffset timestamp);
}
