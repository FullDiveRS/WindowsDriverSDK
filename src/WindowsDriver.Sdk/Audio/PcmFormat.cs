namespace WindowsDriver.Sdk.Audio;

/// <summary>
/// An uncompressed PCM format. Byte order is little-endian (native on Windows);
/// multi-channel data is interleaved.
/// </summary>
public sealed record PcmFormat(int SampleRateHz, int BitsPerSample, int Channels)
{
    public int BytesPerFrame => (BitsPerSample + 7) / 8 * Channels;

    public long BytesPerSecond => (long)SampleRateHz * BytesPerFrame;

    public override string ToString() => $"{SampleRateHz} Hz / {BitsPerSample}-bit / {Channels}ch";
}

/// <summary>Result of a format negotiation attempt.</summary>
public sealed record AudioNegotiationResult
{
    public PcmFormat? Accepted { get; init; }
    public NtStatus Status { get; init; } = NtStatus.Success;
    public string? Reason { get; init; }

    public bool IsAccepted => Status.IsSuccess();
}

/// <summary>
/// Chooses the best supported PCM format for a requested one — the same job
/// IMiniport's DataRangeIntersection does for real audio drivers. Preference
/// order: exact match, then sample rate, then bit depth, then channel count.
/// </summary>
public sealed class AudioFormatNegotiator
{
    private readonly List<PcmFormat> _supported;

    public AudioFormatNegotiator(IEnumerable<PcmFormat> supported)
    {
        _supported = supported.ToList();
        if (_supported.Count == 0)
        {
            throw new ArgumentException("At least one supported format is required.", nameof(supported));
        }
    }

    public IReadOnlyList<PcmFormat> SupportedFormats => _supported;

    public PcmFormat Default => _supported[0];

    /// <summary>Negotiates a concrete format from the request.</summary>
    public AudioNegotiationResult Negotiate(PcmFormat requested)
    {
        if (requested.SampleRateHz <= 0 || requested.BitsPerSample <= 0 || requested.Channels <= 0)
        {
            return new AudioNegotiationResult
            {
                Status = NtStatus.InvalidParameter,
                Reason = $"Format {requested} has non-positive fields.",
            };
        }

        var exact = _supported.FirstOrDefault(f => f == requested);
        if (exact is not null)
        {
            return new AudioNegotiationResult { Accepted = exact };
        }

        // Nothing exact: pick the supported format with the lowest "distance",
        // weighting sample rate heaviest (listeners notice pitch shifts most).
        var best = _supported
            .Select(f => (
                Format: f,
                Score: Math.Abs(f.SampleRateHz - requested.SampleRateHz) * 1.0
                       + Math.Abs(f.BitsPerSample - requested.BitsPerSample) * 100.0
                       + Math.Abs(f.Channels - requested.Channels) * 1000.0))
            .OrderBy(x => x.Score)
            .First();

        return new AudioNegotiationResult
        {
            Accepted = best.Format,
            Status = NtStatus.Success,
            Reason = $"Requested {requested}; closest supported is {best.Format}.",
        };
    }

    /// <summary>True when the exact format is supported (no adjustment needed).</summary>
    public bool IsSupported(PcmFormat format) => _supported.Contains(format);
}
