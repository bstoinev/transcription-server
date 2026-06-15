using System.Runtime.InteropServices;
using System.Text;
using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Runtime;
using AIO.Transcription.Server.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIO.Transcription.Server.Tests.Runtime;

public sealed class WhisperRealAudioIntegrationTests
{
    [Fact]
    public async Task RealAudioStream_ProducesTranscript()
    {
        var options = new WhisperTranscriberOptions
        {
            ModelType = "Tiny",
            AutoDownloadModel = true,
            TargetSampleRate = 16000,
            BoundaryDetectionEnabled = true,
            BoundaryDetectionRmsThreshold = 0.015,
            BoundarySilenceMilliseconds = 700,
            MinimumSpeechMilliseconds = 250,
            MinimumWindowMilliseconds = 3500,
            MaximumSegmentMilliseconds = 15000,
        };

        using var transcriber = new WhisperCppTranscriber(options, NullLogger<WhisperCppTranscriber>.Instance);
        var session = new LiveTranscriptionSession(
            "integration-jfk",
            transcriber,
            options,
            NullLogger<LiveTranscriptionSession>.Instance);

        var wavePath = Path.Combine(AppContext.BaseDirectory, "TestData", "jfk.wav");
        Assert.True(File.Exists(wavePath), $"Expected test audio at {wavePath}");
        EnsureNativeWhisperLibrariesLoaded();

        var envelopes = new List<AIO.Transcription.Server.Contracts.Protocol.ServerEnvelope>();
        foreach (var chunk in ReadWaveChunks(wavePath, chunkMilliseconds: 500))
        {
            var updates = await session.AddAudioChunkAsync(chunk, forceFinalize: false, CancellationToken.None);
            envelopes.AddRange(updates);
        }

        envelopes.AddRange(await session.CompleteAsync(CancellationToken.None));

        var transcript = string.Join(" ", envelopes
            .Where(x => x.Type == "transcript")
            .Select(x => x.TranscriptText)
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        Assert.Contains("your country", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ask not", transcript, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNativeWhisperLibrariesLoaded()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var runtimeDir = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64");
        Assert.True(Directory.Exists(runtimeDir), $"Expected native runtime directory at {runtimeDir}");

        var libraries = new[]
        {
            "libggml-base-whisper.so",
            "libggml-cpu-whisper.so",
            "libggml-whisper.so",
            "libwhisper.so",
        };

        foreach (var library in libraries)
        {
            var fullPath = Path.Combine(runtimeDir, library);
            Assert.True(File.Exists(fullPath), $"Expected native library at {fullPath}");
            NativeLibrary.Load(fullPath);
        }
    }

    private static IEnumerable<AudioChunk> ReadWaveChunks(string wavePath, int chunkMilliseconds)
    {
        using var stream = File.OpenRead(wavePath);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        var riff = new string(reader.ReadChars(4));
        Assert.Equal("RIFF", riff);
        _ = reader.ReadInt32();
        var wave = new string(reader.ReadChars(4));
        Assert.Equal("WAVE", wave);

        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? pcmData = null;

        while (stream.Position < stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                var audioFormat = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadInt32();
                _ = reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
                if (chunkSize > 16)
                {
                    reader.ReadBytes(chunkSize - 16);
                }

                Assert.Equal(1, audioFormat);
            }
            else if (chunkId == "data")
            {
                pcmData = reader.ReadBytes(chunkSize);
            }
            else
            {
                reader.ReadBytes(chunkSize);
            }

            if ((chunkSize & 1) == 1)
            {
                reader.ReadByte();
            }
        }

        Assert.NotNull(pcmData);
        Assert.Equal(1, channels);
        Assert.Equal(16000, sampleRate);
        Assert.Equal(16, bitsPerSample);

        var bytesPerSecond = sampleRate * channels * (bitsPerSample / 8);
        var bytesPerChunk = bytesPerSecond * chunkMilliseconds / 1000;
        for (var offset = 0; offset < pcmData!.Length; offset += bytesPerChunk)
        {
            var count = Math.Min(bytesPerChunk, pcmData.Length - offset);
            var buffer = new byte[count];
            Array.Copy(pcmData, offset, buffer, 0, count);
            yield return new AudioChunk(buffer, count, sampleRate, channels, "pcm_s16le", DateTimeOffset.UtcNow);
        }
    }
}
