using System.Buffers.Binary;

namespace WindowsDriver.Sdk.Audio;

/// <summary>Produces PCM frames into the device ring buffer while running (Capture/Loopback mode).</summary>
public interface IAudioSource
{
    /// <summary>Fills the destination with complete frames; returns the number of frames written.</summary>
    int FillFrames(Span<byte> destination, PcmFormat format);
}

/// <summary>Consumes PCM frames from the device ring buffer (Render mode).</summary>
public interface IAudioSink
{
    void WriteFrames(ReadOnlySpan<byte> frames, PcmFormat format);
}

/// <summary>
/// Continuous sine generator — the virtual "hardware" tone for demos and tests.
/// Phase is accumulated across calls so long captures stay seamless. 16-bit
/// signed little-endian only (the common test format).
/// </summary>
public sealed class SineSource : IAudioSource
{
    private long _framesGenerated;
    private double _phase;

    public SineSource(double frequencyHz = 440.0, double amplitude = 0.25)
    {
        if (amplitude <= 0 || amplitude > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(amplitude), "Amplitude must be in (0, 1].");
        }
        FrequencyHz = frequencyHz;
        Amplitude = amplitude;
    }

    public double FrequencyHz { get; set; }
    public double Amplitude { get; }
    public long FramesGenerated => Interlocked.Read(ref _framesGenerated);

    public int FillFrames(Span<byte> destination, PcmFormat format)
    {
        if (format.BitsPerSample != 16)
        {
            throw new NotSupportedException("SineSource supports 16-bit PCM only; extend FillFrames for other depths.");
        }
        var bytesPerFrame = format.BytesPerFrame;
        var frames = destination.Length / bytesPerFrame;
        var step = 2 * Math.PI * FrequencyHz / format.SampleRateHz;
        for (var f = 0; f < frames; f++)
        {
            var sample = (short)Math.Round(Amplitude * short.MaxValue * Math.Sin(_phase + step * f));
            var offset = f * bytesPerFrame;
            for (var c = 0; c < format.Channels; c++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(offset + c * sizeof(short)), sample);
            }
        }
        _phase += step * frames;
        _phase %= 2 * Math.PI;
        Interlocked.Add(ref _framesGenerated, frames);
        return frames;
    }
}

/// <summary>
/// Applies a linear gain to interleaved 16-bit PCM samples in place, clamping
/// to the 16-bit range. Channel gains can be applied per channel position.
/// </summary>
public static class PcmVolume
{
    public static void ApplyMasterGain16(Span<byte> interleaved16, float gain)
    {
        if (gain >= 0.999f && gain <= 1.001f)
        {
            return;
        }
        for (var i = 0; i + sizeof(short) <= interleaved16.Length; i += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(interleaved16.Slice(i));
            var scaled = Math.Round(sample * gain);
            BinaryPrimitives.WriteInt16LittleEndian(interleaved16.Slice(i), (short)Math.Clamp(scaled, short.MinValue, short.MaxValue));
        }
    }
}

/// <summary>
/// Writes received PCM into a standard 44-byte-header RIFF/WAVE stream — used
/// by the sample and tests to capture the virtual device's render output.
/// </summary>
public sealed class PcmWavWriter : IAudioSink, IDisposable
{
    private readonly Stream _stream;
    private readonly PcmFormat _format;
    private readonly bool _leaveOpen;
    private long _dataBytes;
    private bool _finalized;

    public PcmWavWriter(Stream stream, PcmFormat format, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _format = format;
        _leaveOpen = leaveOpen;
        WriteHeader(_stream, format, dataBytes: 0);
    }

    /// <summary>Bytes of PCM data written so far (excludes the 44-byte header).</summary>
    public long DataBytes => _dataBytes;

    public void WriteFrames(ReadOnlySpan<byte> frames, PcmFormat format)
    {
        if (format != _format)
        {
            throw new InvalidOperationException($"WAV writer was created for {_format} but received {format}.");
        }
        _stream.Write(frames);
        _dataBytes += frames.Length;
    }

    /// <summary>Patches the RIFF chunk sizes so the file is valid. Also called by Dispose.</summary>
    public void WriteTrailer()
    {
        if (_finalized || !_stream.CanSeek)
        {
            return;
        }
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, (int)(36 + _dataBytes));
        _stream.Seek(4, SeekOrigin.Begin);
        _stream.Write(size);
        BinaryPrimitives.WriteInt32LittleEndian(size, (int)_dataBytes);
        _stream.Seek(40, SeekOrigin.Begin);
        _stream.Write(size);
        _stream.Flush();
        _finalized = true;
    }

    public void Dispose()
    {
        WriteTrailer();
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static void WriteHeader(Stream stream, PcmFormat format, long dataBytes)
    {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header.Slice(0));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4), (int)(36 + dataBytes));
        "WAVE"u8.CopyTo(header.Slice(8));
        "fmt "u8.CopyTo(header.Slice(12));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16), 16);                       // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(20), 1);                        // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(22), (short)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24), format.SampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28), (int)format.BytesPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(32), (short)format.BytesPerFrame);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(34), (short)format.BitsPerSample);
        "data"u8.CopyTo(header.Slice(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40), (int)dataBytes);
        stream.Write(header);
    }
}
