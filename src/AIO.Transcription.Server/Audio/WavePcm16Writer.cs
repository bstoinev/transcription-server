using System.Buffers.Binary;

namespace AIO.Transcription.Server.Audio;

public static class WavePcm16Writer
{
    private const string Float32Le = "f32le";
    private const string Pcm16Le = "pcm_s16le";
    private const int WaveHeaderSize = 44;

    public static byte[] WriteWaveFile(IReadOnlyList<AudioChunk> chunks, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (targetSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
        }

        if (chunks.Count == 0)
        {
            return BuildWaveFile([], targetSampleRate);
        }

        var sourceSampleCount = 0;
        foreach (var chunk in chunks)
        {
            sourceSampleCount += GetFrameCount(chunk);
        }

        var monoSamples = new float[sourceSampleCount];
        var sampleOffset = 0;
        foreach (var chunk in chunks)
        {
            sampleOffset += ReadMonoSamples(chunk, monoSamples.AsSpan(sampleOffset));
        }

        var sourceRate = chunks[0].SampleRate;
        var source = sampleOffset == monoSamples.Length ? monoSamples : monoSamples.AsSpan(0, sampleOffset).ToArray();
        var resampled = sourceRate == targetSampleRate
            ? source
            : Resample(source, sourceRate, targetSampleRate);
        return BuildWaveFile(resampled, targetSampleRate);
    }

    public static byte[] WriteWaveFile(IReadOnlyList<float> samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        return BuildWaveFile(samples.ToArray(), sampleRate);
    }

    public static float[] DecodeMonoSamples(AudioChunk chunk, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (targetSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
        }

        var sampleCount = GetFrameCount(chunk);
        var samples = new float[sampleCount];
        var written = ReadMonoSamples(chunk, samples);
        var source = written == samples.Length ? samples : samples.AsSpan(0, written).ToArray();
        return chunk.SampleRate == targetSampleRate
            ? source
            : Resample(source, chunk.SampleRate, targetSampleRate);
    }

    public static double EstimateChunkMilliseconds(AudioChunk chunk)
    {
        var frames = GetFrameCount(chunk);
        return frames * 1000.0 / Math.Max(1, chunk.SampleRate);
    }

    private static int ReadMonoSamples(AudioChunk chunk, Span<float> destination)
    {
        var channels = Math.Max(1, chunk.Channels);
        var encoding = NormalizeEncoding(chunk.Encoding);
        var frameCount = GetFrameCount(chunk);
        if (destination.Length < frameCount)
        {
            throw new InvalidOperationException($"Destination span is too small for {frameCount} decoded samples.");
        }

        if (encoding == Float32Le)
        {
            var bytesPerFrameFloat = channels * sizeof(float);
            for (var frameIndex = 0; frameIndex < frameCount; frameIndex += 1)
            {
                var offset = frameIndex * bytesPerFrameFloat;
                var sum = 0.0f;
                for (var channel = 0; channel < channels; channel += 1)
                {
                    sum += BitConverter.ToSingle(chunk.Buffer, offset + (channel * sizeof(float)));
                }

                destination[frameIndex] = sum / channels;
            }

            return frameCount;
        }

        if (encoding == Pcm16Le)
        {
            var bytesPerFramePcm16 = channels * sizeof(short);
            for (var frameIndex = 0; frameIndex < frameCount; frameIndex += 1)
            {
                var offset = frameIndex * bytesPerFramePcm16;
                var sum = 0.0f;
                for (var channel = 0; channel < channels; channel += 1)
                {
                    sum += BitConverter.ToInt16(chunk.Buffer, offset + (channel * sizeof(short))) / (float)short.MaxValue;
                }

                destination[frameIndex] = sum / channels;
            }

            return frameCount;
        }

        throw new InvalidOperationException($"Unsupported audio encoding: {chunk.Encoding}");
    }

    private static string NormalizeEncoding(string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
        {
            throw new InvalidOperationException("Audio chunk encoding is required.");
        }

        return encoding.Trim().ToLowerInvariant() switch
        {
            "f32le" or "f32" or "float" or "float32" or "float32le" or "ieeefloat" => Float32Le,
            "pcm_s16le" or "pcm16" or "pcm16le" or "s16le" or "int16" => Pcm16Le,
            _ => throw new InvalidOperationException($"Unsupported audio encoding: {encoding}")
        };
    }

    private static int GetFrameCount(AudioChunk chunk)
    {
        var channels = Math.Max(1, chunk.Channels);
        var encoding = NormalizeEncoding(chunk.Encoding);
        var bytesPerSample = encoding == Float32Le ? sizeof(float) : sizeof(short);
        var bytesPerFrame = channels * bytesPerSample;
        ValidateChunkAlignment(chunk, bytesPerFrame);
        return chunk.BytesRecorded / bytesPerFrame;
    }

    private static void ValidateChunkAlignment(AudioChunk chunk, int bytesPerFrame)
    {
        if (chunk.BytesRecorded < 0 || chunk.BytesRecorded > chunk.Buffer.Length)
        {
            throw new InvalidOperationException(
                $"Audio chunk byte count {chunk.BytesRecorded} is outside the buffer length {chunk.Buffer.Length}.");
        }

        if (chunk.BytesRecorded % bytesPerFrame != 0)
        {
            throw new InvalidOperationException(
                $"Audio chunk size {chunk.BytesRecorded} is not aligned to frame size {bytesPerFrame} for {chunk.Encoding}.");
        }
    }

    private static float[] Resample(float[] source, int sourceRate, int targetRate)
    {
        if (source.Length == 0)
        {
            return [];
        }

        var ratio = targetRate / (double)sourceRate;
        var outputLength = Math.Max(1, (int)Math.Round(source.Length * ratio));
        var output = new float[outputLength];
        for (var index = 0; index < outputLength; index += 1)
        {
            var sourcePosition = index / ratio;
            var leftIndex = (int)Math.Floor(sourcePosition);
            var rightIndex = Math.Min(leftIndex + 1, source.Length - 1);
            var fraction = (float)(sourcePosition - leftIndex);
            var left = source[Math.Min(leftIndex, source.Length - 1)];
            var right = source[rightIndex];
            output[index] = left + ((right - left) * fraction);
        }

        return output;
    }

    private static byte[] BuildWaveFile(float[] samples, int sampleRate)
    {
        var pcmByteLength = samples.Length * sizeof(short);
        var waveBytes = new byte[WaveHeaderSize + pcmByteLength];
        WriteWaveHeader(waveBytes.AsSpan(0, WaveHeaderSize), sampleRate, channels: 1, bitsPerSample: 16, pcmByteLength);

        var pcmSpan = waveBytes.AsSpan(WaveHeaderSize);
        for (var index = 0; index < samples.Length; index += 1)
        {
            var clamped = Math.Clamp(samples[index], -1.0f, 1.0f);
            var sample = (short)Math.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcmSpan.Slice(index * sizeof(short), sizeof(short)), sample);
        }

        return waveBytes;
    }

    private static void WriteWaveHeader(Span<byte> destination, int sampleRate, short channels, short bitsPerSample, int pcmByteLength)
    {
        destination[0] = (byte)'R';
        destination[1] = (byte)'I';
        destination[2] = (byte)'F';
        destination[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4, 4), 36 + pcmByteLength);
        destination[8] = (byte)'W';
        destination[9] = (byte)'A';
        destination[10] = (byte)'V';
        destination[11] = (byte)'E';
        destination[12] = (byte)'f';
        destination[13] = (byte)'m';
        destination[14] = (byte)'t';
        destination[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(24, 4), sampleRate);
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(28, 4), byteRate);
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(34, 2), bitsPerSample);
        destination[36] = (byte)'d';
        destination[37] = (byte)'a';
        destination[38] = (byte)'t';
        destination[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(40, 4), pcmByteLength);
    }
}
