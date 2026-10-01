using System.Buffers.Binary;
using VrcVa.Core;
using VrcVa.Windows.Voice;

namespace VrcVa.Windows.Tests;

public sealed class VoiceAudioSessionTests
{
    [Fact]
    public void WaveHeaderIsCanonicalAndQuotaComesOnlyFromPcmSamples()
    {
        using VoiceAudioBuffer buffer = new(2);
        byte[] samples = new byte[32_002];
        for (int index = 0; index < samples.Length; index += 2)
        {
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(index), short.MaxValue);
        }

        Assert.False(buffer.Append(samples));
        buffer.Seal();
        ReadOnlySpan<byte> wav = buffer.WaveBytes.Span;
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav[..4]));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(wav.Slice(8, 8)));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav.Slice(36, 4)));
        Assert.Equal(32_002, BinaryPrimitives.ReadInt32LittleEndian(wav[40..]));
        Assert.Equal(32_038, BinaryPrimitives.ReadInt32LittleEndian(wav[4..]));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(wav[16..]));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav[20..]));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav[22..]));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(wav[24..]));
        Assert.Equal(32_000, BinaryPrimitives.ReadInt32LittleEndian(wav[28..]));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav[32..]));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav[34..]));
        Assert.Equal(2, VoiceAudioFormat.GetQuotaSeconds(buffer.PcmBytes));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void RmsThresholdUsesNormalizedPcm16(int sample, bool silent)
    {
        using VoiceAudioBuffer buffer = new(1);
        byte[] samples = new byte[8];
        for (int index = 0; index < samples.Length; index += 2)
        {
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(index), (short)sample);
        }
        buffer.Append(samples);
        if (silent) { Assert.Equal(VoiceInputFailureCode.SilentAudio, Assert.Throws<VoiceInputException>(buffer.Seal).Code); }
        else { buffer.Seal(); }
    }

    [Fact]
    public void PeakAboveThresholdAcceptsEvenWhenWholeRmsIsLow()
    {
        using VoiceAudioBuffer buffer = new(1);
        byte[] samples = new byte[32_000];
        BinaryPrimitives.WriteInt16LittleEndian(samples, 328);
        buffer.Append(samples);
        buffer.Seal();
    }

    [Fact]
    public void MaximumAllocationIsHardBoundedAndDisposeZerosBorrowedView()
    {
        using VoiceAudioBuffer buffer = new(120);
        byte[] chunk = new byte[VoiceAudioFormat.MaximumPcmDataBytes + 2];
        chunk[1] = 127;
        Assert.True(buffer.Append(chunk));
        buffer.Seal();
        ReadOnlyMemory<byte> bytes = buffer.WaveBytes;
        Assert.Equal(VoiceAudioFormat.MaximumWaveBytes, bytes.Length);
        buffer.Dispose();
        Assert.All(bytes.ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => buffer.WaveBytes);
    }

    [Fact]
    public void FailedAudioExpiryDoesNotExtendOnRetryAndAdmittedLeaseCanFinish()
    {
        ManualTimeProvider time = new();
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? first));
        VoiceAudioBuffer buffer = SpeechBuffer();
        using VoiceAudioSession audio = new(first!.SessionId, buffer,
            new() { FailedAudioRetentionSeconds = 15 }, time);
        Assert.True(audio.TryAcquire(execution, first, out VoiceAudioLease? initial));
        ReadOnlyMemory<byte> bytes = initial!.WaveBytes;
        initial.Dispose();
        audio.MarkTransmissionFailed();
        first.Dispose();
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.True(execution.TryBeginOperation(audio.SessionId, Guid.NewGuid(), out ExecutionOperation? retry));
        using (retry)
        {
            Assert.True(audio.TryAcquire(execution, retry!, out VoiceAudioLease? lease));
            audio.MarkTransmissionFailed(); // cannot restart the first-failure deadline
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.False(audio.IsAvailable);
            Assert.False(audio.TryAcquire(execution, retry!, out _));
            Assert.Contains(bytes.ToArray(), value => value != 0);
            lease!.Dispose();
            Assert.All(bytes.ToArray(), value => Assert.Equal(0, value));
        }
    }

    [Fact]
    public void FailedAudioIsZeroedAtExpiryWithoutAnotherAction()
    {
        ManualTimeProvider time = new();
        VoiceAudioBuffer buffer = SpeechBuffer();
        ReadOnlyMemory<byte> bytes = buffer.WaveBytes;
        using VoiceAudioSession audio = new(Guid.NewGuid(), buffer, new(), time);
        audio.MarkTransmissionFailed();
        time.Advance(TimeSpan.FromSeconds(120));
        Assert.False(audio.IsAvailable);
        Assert.All(bytes.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void StaleSessionForeignOwnerAndDisposedOperationCannotAcquireAudio()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        using VoiceAudioSession audio = new(operation!.SessionId, SpeechBuffer(), new(), TimeProvider.System);
        Assert.False(audio.TryAcquire(new(), operation, out _));
        execution.CloseSession();
        Assert.False(audio.TryAcquire(execution, operation, out _));
        operation.Dispose();
        Assert.False(audio.TryAcquire(execution, operation, out _));
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? newer));
        using (newer) { Assert.False(audio.TryAcquire(execution, newer!, out _)); }
    }

    private static VoiceAudioBuffer SpeechBuffer()
    {
        VoiceAudioBuffer buffer = new(1);
        buffer.Append([0, 64, 0, 192]);
        buffer.Seal();
        return buffer;
    }
}
