namespace WindowsDriver.Sdk.Sensor;

/// <summary>
/// Deterministic 3-axis accelerometer simulator. Gravity is applied on +Z
/// (device lying flat), with smooth seeded sine-noise and optional transient
/// impulses (taps, shakes). Sampling is a pure function of the timestamp:
/// the same timestamp always yields the same values.
/// </summary>
public sealed class AccelerometerSimulator : ISensorSource
{
    private readonly double[] _frequencies;
    private readonly double[] _phases;
    private readonly List<Impulse> _impulses = [];

    public AccelerometerSimulator(int seed = 42, double sampleRateHz = 50)
    {
        var random = new Random(seed);
        _frequencies = new double[3];
        _phases = new double[3];
        for (var i = 0; i < 3; i++)
        {
            // 0.5–4 Hz wobble components per axis
            _frequencies[i] = 0.5 + random.NextDouble() * 3.5;
            _phases[i] = random.NextDouble() * 2 * Math.PI;
        }
        SampleRateHz = sampleRateHz;
    }

    public double SampleRateHz { get; set; }

    /// <summary>Amplitude of the smooth noise in m/s² per axis.</summary>
    public double NoiseLevel { get; set; } = 0.05;

    /// <summary>Gravity magnitude in m/s² (standard gravity).</summary>
    public double GravityMagnitude { get; set; } = 9.80665;

    /// <summary>Adds a transient acceleration impulse starting at <paramref name="at"/> with linear attack/release.</summary>
    public void AddImpulse(TimeSpan at, TimeSpan duration, (double X, double Y, double Z) acceleration)
    {
        _impulses.Add(new Impulse(at, duration, acceleration));
    }

    public SensorKind Kind => SensorKind.Accelerometer3D;
    public string Unit => "m/s^2";
    public int ValueCount => 3;

    public SensorReading Sample(DateTimeOffset timestamp)
    {
        var t = timestamp.TimeOfDay.TotalSeconds;
        var x = Noise(t, 0);
        var y = Noise(t, 1);
        var z = Noise(t, 2) + GravityMagnitude;
        foreach (var impulse in _impulses)
        {
            var contribution = impulse.ValueAt(timestamp.TimeOfDay);
            x += contribution.X;
            y += contribution.Y;
            z += contribution.Z;
        }
        return new SensorReading(Kind, timestamp, [x, y, z], Unit);
    }

    private double Noise(double tSeconds, int axis) =>
        NoiseLevel * Math.Sin(2 * Math.PI * _frequencies[axis] * tSeconds + _phases[axis]);

    private sealed record Impulse(TimeSpan Start, TimeSpan Duration, (double X, double Y, double Z) Accel)
    {
        public (double X, double Y, double Z) ValueAt(TimeSpan time)
        {
            if (time < Start || time > Start + Duration)
            {
                return (0, 0, 0);
            }
            // Triangular envelope: linear rise to peak at the midpoint.
            var p = (time - Start) / Duration;
            var envelope = 2 * (p <= 0.5 ? p : 1 - p);
            return (Accel.X * envelope, Accel.Y * envelope, Accel.Z * envelope);
        }
    }
}

/// <summary>
/// Deterministic ambient light simulator: an indoor baseline with a smooth
/// daylight sine curve (peak at 12:00) plus seeded noise. A manual override
/// wins over the curve — used to simulate a lamp being switched.
/// </summary>
public sealed class AmbientLightSimulator : ISensorSource
{
    private readonly double _frequency;
    private readonly double _phase;

    public AmbientLightSimulator(int seed = 7, double sampleRateHz = 4)
    {
        var random = new Random(seed);
        _frequency = 0.05 + random.NextDouble() * 0.1; // slow flicker
        _phase = random.NextDouble() * 2 * Math.PI;
        SampleRateHz = sampleRateHz;
    }

    public double SampleRateHz { get; set; }

    /// <summary>Indoor baseline illuminance in lux.</summary>
    public double IndoorBaseLux { get; set; } = 400;

    /// <summary>Peak outdoor contribution at solar noon in lux.</summary>
    public double PeakDaylightLux { get; set; } = 12000;

    /// <summary>Manual illuminance override; null follows the day curve.</summary>
    public double? OverrideLux { get; set; }

    public SensorKind Kind => SensorKind.AmbientLight;
    public string Unit => "lx";
    public int ValueCount => 1;

    public SensorReading Sample(DateTimeOffset timestamp)
    {
        if (OverrideLux is { } manual)
        {
            return new SensorReading(Kind, timestamp, [manual], Unit);
        }

        var timeOfDay = timestamp.TimeOfDay;
        // Sun above horizon from ~06:00 to ~18:00, peaking at noon.
        var dayFraction = (timeOfDay - TimeSpan.FromHours(6)) / TimeSpan.FromHours(12);
        var daylight = dayFraction > 0 && dayFraction < 1
            ? Math.Sin(dayFraction * Math.PI) * PeakDaylightLux
            : 0;
        var flicker = IndoorBaseLux * 0.05 * Math.Sin(2 * Math.PI * _frequency * timeOfDay.TotalSeconds + _phase);
        var lux = Math.Max(0, IndoorBaseLux + daylight + flicker);
        return new SensorReading(Kind, timestamp, [lux], Unit);
    }
}
