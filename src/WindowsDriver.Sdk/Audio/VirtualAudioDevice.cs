namespace WindowsDriver.Sdk.Audio;

/// <summary>Direction of the virtual audio device.</summary>
public enum AudioVirtualMode
{
    /// <summary>The source produces audio; clients read frames (capture/loopback-style).</summary>
    Capture,

    /// <summary>Clients write frames; the sink consumes them (render-style).</summary>
    Render,

    /// <summary>Clients write frames that come back on Read — a full loopback.</summary>
    Loopback,
}

/// <summary>Configuration for <see cref="VirtualAudioDevice"/>.</summary>
public sealed class AudioVirtualOptions
{
    public IReadOnlyList<PcmFormat> SupportedFormats { get; init; } =
    [
        new PcmFormat(44100, 16, 2),
        new PcmFormat(48000, 16, 2),
        new PcmFormat(96000, 16, 2),
    ];

    public AudioVirtualMode Mode { get; init; } = AudioVirtualMode.Capture;

    public int RingBufferBytes { get; init; } = 65536;

    /// <summary>Required for Capture/Loopback.</summary>
    public IAudioSource? Source { get; init; }

    /// <summary>Required for Render.</summary>
    public IAudioSink? Sink { get; init; }

    /// <summary>Applies the current master/channel gain on the device output path.</summary>
    public bool ApplyVolumeToOutput { get; init; } = true;
}

/// <summary>
/// A complete virtual audio device built on the SDK: format negotiation, a PCM
/// ring buffer standing in for hardware DMA, a start/stop/pause state machine,
/// volume with ramps, and the <see cref="AudioIoctlCodes"/> IOCTL contract.
///
/// Time is taken from the host's <see cref="System.TimeProvider"/> — production
/// hosts pass a real-time provider, tests pass a controllable fake. Byte position
/// advances with wall time while Running, exactly like a real render/capture
/// clock, and the ring buffer is lazily filled on demand.
/// </summary>
public class VirtualAudioDevice : DeviceBase
{
    private readonly AudioVirtualOptions _options;
    private readonly AudioFormatNegotiator _negotiator;
    private readonly ByteRingBuffer _ring;
    private readonly object _gate = new();
    private long _startTicks;
    private long _pausedAccumTicks;
    private long _pausedAtTicks;
    private long _producedFrames;

    public VirtualAudioDevice(DeviceContext context, AudioVirtualOptions? options = null)
        : base(context)
    {
        _options = options ?? new AudioVirtualOptions();
        _negotiator = new AudioFormatNegotiator(_options.SupportedFormats);
        _ring = new ByteRingBuffer(_options.RingBufferBytes);
        CurrentFormat = _negotiator.Default;
        Volume = new VolumeState(CurrentFormat.Channels, Driver.TimeProvider);

        DefaultQueue
            .OnDeviceControl(HandleDeviceControlAsync)
            .OnRead(HandleReadAsync)
            .OnWrite(HandleWriteAsync);
    }

    public PcmFormat CurrentFormat { get; private set; }

    public AudioDeviceStateMachine State { get; } = new();

    public VolumeState Volume { get; private set; }

    public ByteRingBuffer Ring => _ring;

    public AudioFormatNegotiator Negotiator => _negotiator;

    /// <summary>Bytes the device has produced/consumed while running (the audio position).</summary>
    public long ProducedFrames
    {
        get { lock (_gate) { return _producedFrames; } }
    }

    // ----- Lifecycle -----

    public override Task OnPrepareHardwareAsync(CancellationToken cancellationToken)
    {
        Context.Log.Info("audio device: preparing hardware, {0} supported formats", _negotiator.SupportedFormats.Count);
        return Task.CompletedTask;
    }

    public override Task OnD0EntryAsync(DevicePowerState previousState, CancellationToken cancellationToken)
    {
        Context.Log.Info("audio device: D0 entry from {0}", previousState);
        return Task.CompletedTask;
    }

    public override Task OnD0ExitAsync(DevicePowerState newState, CancellationToken cancellationToken)
    {
        State.Stop();
        Context.Log.Info("audio device: D0 exit to {0}", newState);
        return Task.CompletedTask;
    }

    // ----- IO handlers -----

    private Task HandleReadAsync(IoRequest request, CancellationToken ct)
    {
        if (_options.Mode == AudioVirtualMode.Render)
        {
            request.Fail(NtStatus.InvalidDeviceRequest);
            return Task.CompletedTask;
        }
        PumpTo(now());
        var bytesPerFrame = CurrentFormat.BytesPerFrame;
        var requestedFrames = request.OutputBuffer.Length / bytesPerFrame;
        var destination = request.OutputBuffer.Slice(0, requestedFrames * bytesPerFrame);
        var read = _ring.Read(destination.Span);
        if (_options.ApplyVolumeToOutput)
        {
            PcmVolume.ApplyMasterGain16(destination.Span.Slice(0, read), Volume.CurrentGain);
        }
        request.Complete(NtStatus.Success, read);
        return Task.CompletedTask;
    }

    private Task HandleWriteAsync(IoRequest request, CancellationToken ct)
    {
        if (_options.Mode == AudioVirtualMode.Capture)
        {
            request.Fail(NtStatus.InvalidDeviceRequest);
            return Task.CompletedTask;
        }
        var bytesPerFrame = CurrentFormat.BytesPerFrame;
        var frames = request.InputBuffer.Length / bytesPerFrame;
        var data = request.InputBuffer.Slice(0, frames * bytesPerFrame);
        var accepted = _ring.Write(data.Span);
        PumpTo(now()); // drain toward the sink; dropped frames are counted by the ring
        request.Complete(NtStatus.Success, accepted);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the <see cref="AudioIoctlCodes"/> contract. Subclasses override
    /// this to add device-specific IOCTLs and fall back to base for the rest.
    /// </summary>
    protected virtual Task HandleDeviceControlAsync(IoRequest request, CancellationToken ct)
    {
        var code = request.IoControlCode;
        if (code == AudioIoctlCodes.GetSupportedFormats)
        {
            HandleGetSupportedFormats(request);
        }
        else if (code == AudioIoctlCodes.SetFormat)
        {
            HandleSetFormat(request);
        }
        else if (code == AudioIoctlCodes.GetFormat)
        {
            HandleGetFormat(request);
        }
        else if (code == AudioIoctlCodes.Start)
        {
            State.Start();
            StartClock();
            request.Complete(NtStatus.Success);
        }
        else if (code == AudioIoctlCodes.Stop)
        {
            State.Stop();
            request.Complete(NtStatus.Success);
        }
        else if (code == AudioIoctlCodes.Pause)
        {
            PauseClock();
            State.Pause();
            request.Complete(NtStatus.Success);
        }
        else if (code == AudioIoctlCodes.GetPosition)
        {
            HandleGetPosition(request);
        }
        else if (code == AudioIoctlCodes.SetVolume)
        {
            HandleSetVolume(request);
        }
        else if (code == AudioIoctlCodes.GetVolume)
        {
            HandleGetVolume(request);
        }
        else if (code == AudioIoctlCodes.SkipOutput)
        {
            var bytes = WireFormat.ReadUInt32(request.InputBuffer.Span, 0);
            var skipped = _ring.Skip((int)Math.Min(bytes, int.MaxValue));
            request.Complete(NtStatus.Success, sizeof(uint));
            Context.Log.Trace("skipped {0} output bytes", skipped);
        }
        else
        {
            request.Fail(NtStatus.InvalidDeviceRequest);
        }
        return Task.CompletedTask;
    }

    private void HandleGetSupportedFormats(IoRequest request)
    {
        var formats = _negotiator.SupportedFormats;
        var needed = sizeof(uint) + formats.Count * 12;
        if (request.OutputBuffer.Length < needed)
        {
            request.Fail(NtStatus.BufferTooSmall);
            return;
        }
        var span = request.OutputBuffer.Span;
        WireFormat.WriteUInt32(span, 0, (uint)formats.Count);
        for (var i = 0; i < formats.Count; i++)
        {
            var baseOffset = sizeof(uint) + i * 12;
            WireFormat.WriteUInt32(span, baseOffset, (uint)formats[i].SampleRateHz);
            WireFormat.WriteUInt32(span, baseOffset + 4, (uint)formats[i].BitsPerSample);
            WireFormat.WriteUInt32(span, baseOffset + 8, (uint)formats[i].Channels);
        }
        request.Complete(NtStatus.Success, needed);
    }

    private void HandleSetFormat(IoRequest request)
    {
        if (request.InputBuffer.Length < 12)
        {
            request.Fail(NtStatus.BufferTooSmall);
            return;
        }
        if (State.Snapshot() != AudioDeviceState.Stopped)
        {
            request.Fail(NtStatus.InvalidDeviceState);
            return;
        }
        var requested = new PcmFormat(
            (int)WireFormat.ReadUInt32(request.InputBuffer.Span, 0),
            (int)WireFormat.ReadUInt32(request.InputBuffer.Span, 4),
            (int)WireFormat.ReadUInt32(request.InputBuffer.Span, 8));

        var result = _negotiator.Negotiate(requested);
        if (!result.IsAccepted || result.Accepted is null)
        {
            request.Fail(result.Status);
            return;
        }
        CurrentFormat = result.Accepted;
        Volume = new VolumeState(CurrentFormat.Channels, Driver.TimeProvider);
        var span = request.OutputBuffer.Span;
        if (span.Length >= 12)
        {
            WireFormat.WriteUInt32(span, 0, (uint)CurrentFormat.SampleRateHz);
            WireFormat.WriteUInt32(span, 4, (uint)CurrentFormat.BitsPerSample);
            WireFormat.WriteUInt32(span, 8, (uint)CurrentFormat.Channels);
            request.Complete(result.Status, 12); // BufferOverflow signals "adjusted"
        }
        else
        {
            request.Complete(result.Status);
        }
    }

    private void HandleGetFormat(IoRequest request)
    {
        if (request.OutputBuffer.Length < 12)
        {
            request.Fail(NtStatus.BufferTooSmall);
            return;
        }
        var span = request.OutputBuffer.Span;
        WireFormat.WriteUInt32(span, 0, (uint)CurrentFormat.SampleRateHz);
        WireFormat.WriteUInt32(span, 4, (uint)CurrentFormat.BitsPerSample);
        WireFormat.WriteUInt32(span, 8, (uint)CurrentFormat.Channels);
        request.Complete(NtStatus.Success, 12);
    }

    private void HandleGetPosition(IoRequest request)
    {
        if (request.OutputBuffer.Length < 20)
        {
            request.Fail(NtStatus.BufferTooSmall);
            return;
        }
        PumpTo(now());
        long produced;
        lock (_gate)
        {
            produced = _producedFrames;
        }
        var span = request.OutputBuffer.Span;
        WireFormat.WriteUInt64(span, 0, (ulong)(produced * CurrentFormat.BytesPerFrame));
        WireFormat.WriteUInt64(span, 8, (ulong)produced);
        WireFormat.WriteUInt32(span, 16, (uint)State.Snapshot());
        request.Complete(NtStatus.Success, 20);
    }

    private void HandleSetVolume(IoRequest request)
    {
        var min = 2 * sizeof(float) + sizeof(byte) + sizeof(uint) + sizeof(uint);
        if (request.InputBuffer.Length < min)
        {
            request.Fail(NtStatus.BufferTooSmall);
            return;
        }
        var span = request.InputBuffer.Span;
        var masterGain = WireFormat.ReadFloat(span, 0);
        var mute = span[8] != 0;
        var rampMs = WireFormat.ReadUInt32(span, 9);
        var channelCount = (int)WireFormat.ReadUInt32(span, 13);
        var needed = min + channelCount * sizeof(float);
        if (request.InputBuffer.Length < needed)
        {
            request.Fail(NtStatus.BufferTooSmall);
            return;
        }
        if (channelCount > Volume.Channels)
        {
            request.Fail(NtStatus.InvalidParameter);
            return;
        }
        Volume.SetMasterGain(masterGain, TimeSpan.FromMilliseconds(rampMs));
        Volume.SetMute(mute);
        for (var c = 0; c < channelCount; c++)
        {
            Volume.SetChannelGain(c, WireFormat.ReadFloat(span, min + c * sizeof(float)));
        }
        request.Complete(NtStatus.Success);
    }

    private void HandleGetVolume(IoRequest request)
    {
        var needed = 2 * sizeof(float) + sizeof(byte) + sizeof(uint) + sizeof(uint) + Volume.Channels * sizeof(float);
        if (request.OutputBuffer.Length < needed)
        {
            request.Fail(NtStatus.BufferTooSmall);
            return;
        }
        var span = request.OutputBuffer.Span;
        WireFormat.WriteFloat(span, 0, Volume.TargetGain);
        WireFormat.WriteFloat(span, 4, Volume.CurrentGain);
        span[8] = Volume.Mute ? (byte)1 : (byte)0;
        WireFormat.WriteUInt32(span, 9, (uint)Volume.RampRemaining.TotalMilliseconds);
        WireFormat.WriteUInt32(span, 13, (uint)Volume.Channels);
        for (var c = 0; c < Volume.Channels; c++)
        {
            WireFormat.WriteFloat(span, 17 + c * sizeof(float), Volume.GetChannelGain(c));
        }
        request.Complete(NtStatus.Success, needed);
    }

    // ----- Clock / ring filling -----

    private DateTimeOffset now() => Driver.TimeProvider.GetUtcNow();

    private void StartClock()
    {
        lock (_gate)
        {
            _startTicks = now().UtcTicks;
            _pausedAccumTicks = 0;
        }
    }

    private void PauseClock()
    {
        lock (_gate)
        {
            _pausedAtTicks = now().UtcTicks;
        }
    }

    /// <summary>
    /// Advances the virtual hardware clock: produces frames up to "now" into (or
    /// out of) the ring buffer. Called lazily from Read/Write/GetPosition.
    /// </summary>
    private void PumpTo(DateTimeOffset timestamp)
    {
        if (State.Snapshot() != AudioDeviceState.Running)
        {
            return;
        }
        long elapsedTicks;
        lock (_gate)
        {
            var start = _startTicks + _pausedAccumTicks;
            elapsedTicks = timestamp.UtcTicks - start;
            if (elapsedTicks <= 0)
            {
                return;
            }
        }

        var bytesPerSecond = CurrentFormat.BytesPerSecond;
        var targetFrames = (long)(elapsedTicks * bytesPerSecond / TimeSpan.TicksPerSecond / CurrentFormat.BytesPerFrame);
        long produced;
        lock (_gate)
        {
            produced = targetFrames - _producedFrames;
            if (produced <= 0)
            {
                return;
            }
        }

        var bytesNeeded = checked((int)Math.Min(produced * CurrentFormat.BytesPerFrame, int.MaxValue));
        var buffer = new byte[bytesNeeded];

        switch (_options.Mode)
        {
            case AudioVirtualMode.Capture or AudioVirtualMode.Loopback:
            {
                var source = _options.Source ?? throw new InvalidOperationException("Capture/Loopback requires a source.");
                var framesFilled = source.FillFrames(buffer, CurrentFormat);
                _ring.Write(buffer.AsSpan(0, framesFilled * CurrentFormat.BytesPerFrame));
                lock (_gate)
                {
                    _producedFrames += framesFilled;
                }
                break;
            }
            case AudioVirtualMode.Render:
            {
                var sink = _options.Sink ?? throw new InvalidOperationException("Render requires a sink.");
                var consumed = _ring.Read(buffer);
                sink.WriteFrames(buffer.AsSpan(0, consumed), CurrentFormat);
                lock (_gate)
                {
                    _producedFrames += consumed / CurrentFormat.BytesPerFrame;
                }
                break;
            }
        }
    }
}
