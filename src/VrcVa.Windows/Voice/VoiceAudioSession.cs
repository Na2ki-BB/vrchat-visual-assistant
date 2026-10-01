using System.Buffers.Binary;
using System.Security.Cryptography;
using VrcVa.Core;

namespace VrcVa.Windows.Voice;

/// <summary>One bounded WAV allocation. No export, file, logging, or content string APIs.</summary>
internal sealed class VoiceAudioBuffer : IDisposable
{
    private byte[]? _bytes;
    private readonly int _pcmLimit;
    private int _pcmBytes;
    private bool _sealed;

    public VoiceAudioBuffer(int maximumRecordingSeconds)
    {
        _pcmLimit = VoiceAudioFormat.GetPcmByteLimit(maximumRecordingSeconds);
        _bytes = new byte[_pcmLimit + VoiceAudioFormat.WaveHeaderBytes];
    }

    public int PcmBytes => _pcmBytes;

    public ReadOnlyMemory<byte> WaveBytes => _sealed && _bytes is not null
        ? _bytes.AsMemory(0, VoiceAudioFormat.WaveHeaderBytes + _pcmBytes)
        : throw new ObjectDisposedException(nameof(VoiceAudioBuffer));

    public bool Append(ReadOnlySpan<byte> pcm)
    {
        if (_sealed || _bytes is null) { throw new ObjectDisposedException(nameof(VoiceAudioBuffer)); }
        if (pcm.Length % VoiceAudioFormat.BlockAlignment != 0)
        {
            throw new VoiceInputException(VoiceInputFailureCode.InvalidAudio);
        }

        int count = Math.Min(pcm.Length, _pcmLimit - _pcmBytes);
        pcm[..count].CopyTo(_bytes.AsSpan(VoiceAudioFormat.WaveHeaderBytes + _pcmBytes));
        _pcmBytes += count;
        return _pcmBytes == _pcmLimit;
    }

    public void Seal()
    {
        if (_bytes is null) { throw new ObjectDisposedException(nameof(VoiceAudioBuffer)); }
        if (_sealed) { return; }
        if (_pcmBytes == 0) { throw new VoiceInputException(VoiceInputFailureCode.EmptyAudio); }
        double squares = 0;
        double peak = 0;
        ReadOnlySpan<byte> pcm = _bytes.AsSpan(VoiceAudioFormat.WaveHeaderBytes, _pcmBytes);
        for (int offset = 0; offset < pcm.Length; offset += VoiceAudioFormat.BlockAlignment)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..]) / 32768d;
            squares += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        if (Math.Sqrt(squares / (_pcmBytes / VoiceAudioFormat.BlockAlignment)) <= 0.001
            && peak <= 0.01)
        {
            throw new VoiceInputException(VoiceInputFailureCode.SilentAudio);
        }

        Span<byte> header = _bytes.AsSpan(0, VoiceAudioFormat.WaveHeaderBytes);
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], _pcmBytes + 36);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], VoiceAudioFormat.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], VoiceAudioFormat.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], VoiceAudioFormat.BytesPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], VoiceAudioFormat.BlockAlignment);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], VoiceAudioFormat.BitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], _pcmBytes);
        _sealed = true;
    }

    public void Dispose()
    {
        byte[]? bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null) { CryptographicOperations.ZeroMemory(bytes); }
    }
}

/// <summary>
/// Failed input lives only in the current session. The first failure starts a monotonic,
/// non-renewable deadline; a lease admitted before expiry may finish before zeroization.
/// </summary>
internal sealed class VoiceAudioSession : IDisposable
{
    private readonly object _sync = new();
    private readonly VoiceAudioBuffer _buffer;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private ITimer? _expiryTimer;
    private long? _failedAt;
    private int _leases;
    private bool _discarded;

    public VoiceAudioSession(Guid sessionId, VoiceAudioBuffer buffer, VoiceInputOptions options, TimeProvider time)
    {
        SessionId = sessionId;
        _buffer = buffer;
        _time = time;
        _retention = TimeSpan.FromSeconds(options.FailedAudioRetentionSeconds);
    }

    public Guid SessionId { get; }

    public bool IsAvailable
    {
        get { lock (_sync) { CheckExpiry(); return !_discarded; } }
    }

    public bool TryAcquire(ExecutionCoordinator execution, ExecutionOperation operation, out VoiceAudioLease? lease)
    {
        lock (_sync)
        {
            CheckExpiry();
            lease = null;
            if (_discarded || operation.SessionId != SessionId || !execution.IsActive(operation) || !operation.IsCurrent)
            {
                return false;
            }

            _leases++;
            lease = new VoiceAudioLease(this, _buffer.WaveBytes, _buffer.PcmBytes);
            return true;
        }
    }

    public void MarkTransmissionFailed()
    {
        lock (_sync)
        {
            if (_discarded || _failedAt.HasValue) { return; }
            _failedAt = _time.GetTimestamp();
            _expiryTimer = _time.CreateTimer(_ => Dispose(), null, _retention, Timeout.InfiniteTimeSpan);
        }
    }

    private void CheckExpiry()
    {
        if (_failedAt is long failedAt && _time.GetElapsedTime(failedAt) >= _retention) { Discard(); }
    }

    private void Discard()
    {
        _discarded = true;
        _expiryTimer?.Dispose();
        _expiryTimer = null;
        if (_leases == 0) { _buffer.Dispose(); }
    }

    internal void Release()
    {
        lock (_sync)
        {
            _leases--;
            CheckExpiry();
            if (_discarded && _leases == 0) { _buffer.Dispose(); }
        }
    }

    public void Dispose() { lock (_sync) { Discard(); } }
}

internal sealed class VoiceAudioLease : IDisposable
{
    private VoiceAudioSession? _owner;
    internal VoiceAudioLease(VoiceAudioSession owner, ReadOnlyMemory<byte> waveBytes, int pcmBytes)
    {
        _owner = owner;
        WaveBytes = waveBytes;
        PcmBytes = pcmBytes;
    }

    public ReadOnlyMemory<byte> WaveBytes { get; }
    public int PcmBytes { get; }
    public int QuotaSeconds => VoiceAudioFormat.GetQuotaSeconds(PcmBytes);
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
}
