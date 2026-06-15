using System.Buffers.Binary;
using System.Text;

namespace AIO.Transcription.Server.Audio;

public static class WavePcm16Writer
{
    public static byte[] WriteWaveFile(IReadOnlyList<AudioChunk> chunks, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (targetSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
        }

        var monoSamples = new List<float>();
        foreach (var chunk in chunks)
        {
            monoSamples.AddRange(ReadMonoSamples(chunk));
        }

        var sourceRate = chunks.Count == 0 ? targetSampleRate : chunks[0].SampleRate;
        var resampled = Resample(monoSamples, sourceRate, targetSampleRate);
        var pcmBytes = new byte[resampled.Length * sizeof(short)];
        for (var index = 0; index < resampled.Length; index += 1)
        {
            var clamped = Math.Clamp(resampled[index], -1.0f, 1.0f);
            var sample = (short)Math.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcmBytes.AsSpan(index * sizeof(short), sizeof(short)), sample);
        }

        return BuildWaveFile(pcmBytes, targetSampleRate, 1, 16);
    }

    public static double EstimateChunkMilliseconds(AudioChunk chunk)
    {
        var channels = Math.Max(1, chunk.Channels);
        var bytesPerSample = chunk.Encoding.Equals("f32le", StringComparison.OrdinalIgnoreCase) ? sizeof(float) : sizeof(short);
        var frames = chunk.BytesRecorded / Math.Max(1, channels * bytesPerSample);
        return frames * 1000.0 / Math.Max(1, chunk.SampleRate);
    }

    public static double EstimateRootMeanSquare(AudioChunk chunk)
    {
        var sampleCount = 0;
        var squareSum = 0.0;
        foreach (var sample in ReadMonoSamples(chunk))
        {
            squareSum += sample * sample;
            sampleCount += 1;
        }

        if (sampleCount == 0)
        {
            return 0.0;
        }

        return Math.Sqrt(squareSum / sampleCount);
    }

    public static bool HasSpeech(AudioChunk chunk, double rmsThreshold)
    {
        return EstimateRootMeanSquare(chunk) >= Math.Max(0.0, rmsThreshold);
    }

    private static IEnumerable<float> ReadMonoSamples(AudioChunk chunk)
    {
        var channels = Math.Max(1, chunk.Channels);
        if (chunk.Encoding.Equals("f32le", StringComparison.OrdinalIgnoreCase))
        {
            var bytesPerFrameFloat = channels * sizeof(float);
            var usableBytes = chunk.BytesRecorded - (chunk.BytesRecorded % bytesPerFrameFloat);
            for (var offset = 0; offset < usableBytes; offset += bytesPerFrameFloat)
            {
                var sum = 0.0f;
                for (var channel = 0; channel < channels; channel += 1)
                {
                    sum += BitConverter.ToSingle(chunk.Buffer, offset + (channel * sizeof(float)));
                }

                yield return sum / channels;
            }

            yield break;
        }

        if (chunk.Encoding.Equals("pcm_s16le", StringComparison.OrdinalIgnoreCase))
        {
            var bytesPerFramePcm16 = channels * sizeof(short);
            var usableBytes = chunk.BytesRecorded - (chunk.BytesRecorded % bytesPerFramePcm16);
            for (var offset = 0; offset < usableBytes; offset += bytesPerFramePcm16)
            {
                var sum = 0.0f;
                for (var channel = 0; channel < channels; channel += 1)
                {
                    sum += BitConverter.ToInt16(chunk.Buffer, offset + (channel * sizeof(short))) / (float)short.MaxValue;
                }

                yield return sum / channels;
            }

            yield break;
        }

        throw new InvalidOperationException($"Unsupported audio encoding: {chunk.Encoding}");
    }

    private static float[] Resample(IReadOnlyList<float> source, int sourceRate, int targetRate)
    {
        if (source.Count == 0)
        {
            return [];
        }

        if (sourceRate == targetRate)
        {
            return source.ToArray();
        }

        var ratio = targetRate / (double)sourceRate;
        var outputLength = Math.Max(1, (int)Math.Round(source.Count * ratio));
        var output = new float[outputLength];
        for (var index = 0; index < outputLength; index += 1)
        {
            var sourcePosition = index / ratio;
            var leftIndex = (int)Math.Floor(sourcePosition);
            var rightIndex = Math.Min(leftIndex + 1, source.Count - 1);
            var fraction = (float)(sourcePosition - leftIndex);
            var left = source[Math.Min(leftIndex, source.Count - 1)];
            var right = source[rightIndex];
            output[index] = left + ((right - left) * fraction);
        }

        return output;
    }

    private static byte[] BuildWaveFile(byte[] pcmBytes, int sampleRate, short channels, short bitsPerSample)
    {
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmBytes.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmBytes.Length);
        writer.Write(pcmBytes);
        writer.Flush();
        return stream.ToArray();
    }
}
