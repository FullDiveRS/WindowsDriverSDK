namespace WindowsDriver.Sdk.Audio;

/// <summary>Playback state machine states (mirrors KSSTATE: stop/pause/run).</summary>
public enum AudioDeviceState
{
    Stopped,
    Paused,
    Running,
}

/// <summary>
/// Small explicit state machine for audio start/stop/pause transitions.
/// Invalid transitions throw <see cref="DriverException"/> with
/// <see cref="NtStatus.InvalidDeviceState"/> so IOCTL handlers can simply let
/// it bubble to request completion.
/// </summary>
public sealed class AudioDeviceStateMachine
{
    private readonly object _gate = new();

    public AudioDeviceState State { get; private set; } = AudioDeviceState.Stopped;

    public event Action<AudioDeviceState, AudioDeviceState>? Transitioned;

    /// <summary>Stopped/Paused → Running. Starting while already running is invalid.</summary>
    public void Start()
    {
        Transition(AudioDeviceState.Running, allowedFrom: [AudioDeviceState.Stopped, AudioDeviceState.Paused]);
    }

    /// <summary>Running → Paused.</summary>
    public void Pause()
    {
        Transition(AudioDeviceState.Paused, allowedFrom: [AudioDeviceState.Running]);
    }

    /// <summary>Any → Stopped.</summary>
    public void Stop()
    {
        Transition(AudioDeviceState.Stopped, allowedFrom: [AudioDeviceState.Running, AudioDeviceState.Paused, AudioDeviceState.Stopped]);
    }

    public AudioDeviceState Snapshot() { lock (_gate) { return State; } }

    private void Transition(AudioDeviceState target, AudioDeviceState[] allowedFrom)
    {
        AudioDeviceState previous;
        lock (_gate)
        {
            previous = State;
            if (previous == target)
            {
                return; // idempotent
            }
            if (!allowedFrom.Contains(previous))
            {
                throw new DriverException(NtStatus.InvalidDeviceState, $"Cannot transition {previous} → {target}.");
            }
            State = target;
        }
        Transitioned?.Invoke(previous, target);
    }
}

/// <summary>
/// Master/channel volume with dB conversion and a linear ramp, kept separate
/// from the device so tests can drive it directly.
/// </summary>
public sealed class VolumeState
{
    private readonly object _gate = new();
    private float[] _channelGains;
    private float _rampStartGain;
    private float _rampTargetGain;
    private TimeSpan _rampDuration;
    private long _rampStartTimestampTicks;

    public VolumeState(int channels, TimeProvider? timeProvider = null)
    {
        Channels = channels;
        TimeProvider = timeProvider ?? TimeProvider.System;
        _channelGains = new float[channels];
        Array.Fill(_channelGains, 1f);
        _rampTargetGain = 1f;
        _rampStartGain = 1f;
    }

    public TimeProvider TimeProvider { get; }
    public int Channels { get; }

    public bool Mute { get; private set; }

    /// <summary>Linear ramp target (0..1). Reaches the target after <see cref="RampRemaining"/>.</summary>
    public float TargetGain { get; private set; } = 1f;

    /// <summary>Current gain including the in-progress ramp and mute (0..1).</summary>
    public float CurrentGain
    {
        get
        {
            lock (_gate)
            {
                var gain = _rampDuration <= TimeSpan.Zero
                    ? _rampTargetGain
                    : Lerp(_rampStartGain, _rampTargetGain, Progress());
                return Mute ? 0f : Clamp(gain);
            }
        }
    }

    /// <summary>Remaining ramp time; zero when no ramp is in progress.</summary>
    public TimeSpan RampRemaining
    {
        get
        {
            lock (_gate)
            {
                if (_rampDuration <= TimeSpan.Zero)
                {
                    return TimeSpan.Zero;
                }
                var remaining = _rampDuration - (TimeProvider.GetUtcNow() - new DateTimeOffset(_rampStartTimestampTicks, TimeSpan.Zero));
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Sets the master target gain (clamped to 0..1) and starts a ramp.</summary>
    public void SetMasterGain(float target, TimeSpan ramp = default)
    {
        lock (_gate)
        {
            _rampStartGain = CurrentGain;
            _rampTargetGain = Clamp(target);
            _rampDuration = ramp;
            _rampStartTimestampTicks = TimeProvider.GetUtcNow().UtcTicks;
            TargetGain = _rampTargetGain;
        }
    }

    public void SetMute(bool mute)
    {
        lock (_gate)
        {
            Mute = mute;
        }
    }

    public float GetChannelGain(int channel) { lock (_gate) { return Clamp(_channelGains[channel]); } }

    public void SetChannelGain(int channel, float gain)
    {
        lock (_gate)
        {
            _channelGains[channel] = Clamp(gain);
        }
    }

    /// <summary>20·log10(gain) dB, or negative infinity for silence.</summary>
    public double CurrentGainDecibels
    {
        get
        {
            var g = CurrentGain;
            return g <= 0f ? double.NegativeInfinity : 20 * Math.Log10(g);
        }
    }

    private double Progress()
    {
        var elapsedTicks = TimeProvider.GetUtcNow().UtcTicks - _rampStartTimestampTicks;
        var progress = elapsedTicks / (double)_rampDuration.Ticks;
        return Math.Clamp(progress, 0.0, 1.0);
    }

    private static float Clamp(float gain) => Math.Clamp(gain, 0f, 1f);

    private static float Lerp(float a, float b, double t) => (float)(a + (b - a) * t);
}
