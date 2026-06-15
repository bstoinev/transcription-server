using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Runtime;
using AIO.Transcription.Server.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIO.Transcription.Server.Tests.Runtime;

public sealed class LiveTranscriptionSessionTests
{
    [Fact]
    public async Task AddAudioChunkAsync_IgnoresLeadingSilence()
    {
        var transcriber = new FakeWaveTranscriber("unused");
        var session = CreateSession(transcriber);

        var updates = await session.AddAudioChunkAsync(CreateChunk(amplitude: 0.0f, durationMilliseconds: 250), forceFinalize: false, CancellationToken.None);

        Assert.Empty(updates);
        Assert.Equal(0, transcriber.CallCount);
    }

    [Fact]
    public async Task AddAudioChunkAsync_EmitsFinalTranscriptAfterSilenceBoundary()
    {
        var transcriber = new FakeWaveTranscriber("hello world");
        var session = CreateSession(transcriber);

        await session.AddAudioChunkAsync(CreateChunk(amplitude: 0.25f, durationMilliseconds: 250), forceFinalize: false, CancellationToken.None);
        await session.AddAudioChunkAsync(CreateChunk(amplitude: 0.20f, durationMilliseconds: 250), forceFinalize: false, CancellationToken.None);
        var updates = await session.AddAudioChunkAsync(CreateChunk(amplitude: 0.0f, durationMilliseconds: 800), forceFinalize: false, CancellationToken.None);

        var update = Assert.Single(updates);
        Assert.Equal("transcript", update.Type);
        Assert.Equal("hello world", update.TranscriptText);
        Assert.True(update.IsFinal);
        Assert.Equal(1, transcriber.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_FlushesBufferedSpeechAsFinalTranscript()
    {
        var transcriber = new FakeWaveTranscriber("forced final");
        var session = CreateSession(transcriber);

        await session.AddAudioChunkAsync(CreateChunk(amplitude: 0.30f, durationMilliseconds: 300), forceFinalize: false, CancellationToken.None);
        var updates = await session.CompleteAsync(CancellationToken.None);

        var update = Assert.Single(updates);
        Assert.Equal("forced final", update.TranscriptText);
        Assert.True(update.IsFinal);
        Assert.Equal(1, transcriber.CallCount);
    }

    private static LiveTranscriptionSession CreateSession(FakeWaveTranscriber transcriber)
    {
        var options = new WhisperTranscriberOptions
        {
            BoundaryDetectionEnabled = true,
            BoundaryDetectionRmsThreshold = 0.015,
            BoundarySilenceMilliseconds = 700,
            MinimumSpeechMilliseconds = 200,
            MinimumWindowMilliseconds = 5000,
            MaximumSegmentMilliseconds = 15000,
            TargetSampleRate = 16000,
        };

        return new LiveTranscriptionSession("test-session", transcriber, options, NullLogger<LiveTranscriptionSession>.Instance);
    }

    private static AudioChunk CreateChunk(float amplitude, int durationMilliseconds, int sampleRate = 16000)
    {
        var frameCount = sampleRate * durationMilliseconds / 1000;
        var buffer = new byte[frameCount * sizeof(float)];
        for (var index = 0; index < frameCount; index += 1)
        {
            BitConverter.GetBytes(amplitude).CopyTo(buffer, index * sizeof(float));
        }

        return new AudioChunk(buffer, buffer.Length, sampleRate, 1, "f32le", DateTimeOffset.UtcNow);
    }

    private sealed class FakeWaveTranscriber : IWaveTranscriber
    {
        private readonly Queue<string> results;

        public FakeWaveTranscriber(params string[] results)
        {
            this.results = new Queue<string>(results);
        }

        public int CallCount { get; private set; }

        public Task<string> TranscribeWaveAsync(byte[] waveBytes, CancellationToken cancellationToken)
        {
            CallCount += 1;
            var result = results.Count > 0 ? results.Dequeue() : string.Empty;
            return Task.FromResult(result);
        }
    }
}
