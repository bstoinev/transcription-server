using System.Text.Json;
using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Runtime;
using AIO.Transcription.Server.Transcription;
using LogMachina;
using Xunit;

namespace AIO.Transcription.Server.Tests;

public sealed class LiveTranscriptionSessionTests
{
    [Fact]
    public async Task FinalizesOneUtteranceAcrossMultipleTransportChunks()
    {
        var transcriber = new RecordingTranscriber();
        await using var session = CreateSession(transcriber);

        await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 4);
        await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
        await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

        var updates = await ReadAllUpdatesAsync(session);
        Assert.Single(updates, x => x.Type == "final-transcript");
        Assert.DoesNotContain(updates, x => x.Type == "transcript");
    }

    [Fact]
    public async Task PartialTranscriptionUsesRollingContextWindow()
    {
        var transcriber = new RecordingTranscriber();
        var options = CreateOptions();
        await using var session = CreateSession(transcriber, options);

        await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 28);
        await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
        await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

        var partialRequests = transcriber.Requests
            .Take(Math.Max(0, transcriber.Requests.Count - 1))
            .Select(x => x.DurationMs)
            .ToArray();
        Assert.Contains(partialRequests, x => x > 500);
        Assert.All(partialRequests, x => Assert.True(x <= options.PartialWindowMs + 25, $"Partial window was {x} ms."));
    }

    [Fact]
    public async Task EmitsFinalOnlyAfterEndpointing()
    {
        var transcriber = new RecordingTranscriber();
        await using var session = CreateSession(transcriber);

        await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 4);
        var earlyUpdates = await ReadSomeUpdatesAsync(session, TimeSpan.FromMilliseconds(250));
        Assert.DoesNotContain(earlyUpdates, x => x.Type == "final-transcript");

        await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
        await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

        var updates = earlyUpdates.Concat(await ReadAllUpdatesAsync(session)).ToArray();
        Assert.Single(updates, x => x.Type == "final-transcript");
    }

    [Fact]
    public async Task DropsSpeechBelowMinimumUtterance()
    {
        var transcriber = new RecordingTranscriber();
        await using var session = CreateSession(transcriber);

        await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 2);
        await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
        await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

        var updates = await ReadAllUpdatesAsync(session);
        Assert.DoesNotContain(updates, x => x.Type == "final-transcript");
    }

    [Fact]
    public async Task MaxUtteranceForcesFinalization()
    {
        var transcriber = new RecordingTranscriber();
        var options = CreateOptions();
        options.MinimumUtteranceMs = 500;
        options.MaxUtteranceMs = 2000;
        await using var session = CreateSession(transcriber, options);

        await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 5);
        await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

        var updates = await ReadAllUpdatesAsync(session);
        Assert.Contains(updates, x => x.Type == "final-transcript");
    }

    [Fact]
    public async Task FinalUtteranceIncludesPreRollAndPostRoll()
    {
        var transcriber = new RecordingTranscriber();
        await using var session = CreateSession(transcriber);

        await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 1);
        await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 4);
        await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
        await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

        var finalRequest = transcriber.Requests.Last();
        Assert.InRange(finalRequest.DurationMs, 3150, 3250);
    }

    [Fact]
    public void SerializesExplicitTranscriptEventsWithoutIsFinal()
    {
        var partial = new ServerEnvelope
        {
            Type = "partial-transcript",
            SessionId = "session-1",
            UtteranceId = "utt-1",
            Sequence = 1,
            TranscriptText = "preview",
            ModelType = "base.en",
            ReceivedChunkCount = 2,
            ReceivedAudioBytes = 100
        };
        var final = new ServerEnvelope
        {
            Type = "final-transcript",
            SessionId = "session-1",
            UtteranceId = "utt-1",
            TranscriptText = "final",
            ModelType = "base.en",
            ReceivedChunkCount = 4,
            ReceivedAudioBytes = 200
        };

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var partialJson = JsonSerializer.Serialize(partial, options);
        var finalJson = JsonSerializer.Serialize(final, options);

        Assert.Contains("\"type\":\"partial-transcript\"", partialJson);
        Assert.Contains("\"sequence\":1", partialJson);
        Assert.Contains("\"type\":\"final-transcript\"", finalJson);
        Assert.DoesNotContain("isFinal", partialJson);
        Assert.DoesNotContain("isFinal", finalJson);
    }

    [Fact]
    public async Task PromptHistoryUpdatesOnlyFromFinalTranscripts()
    {
        var transcriber = new RecordingTranscriber();
        var options = CreateOptions();
        options.TechnicalPrompt = "technical terms: Kubernetes, gRPC";
        await using var session = CreateSession(transcriber, options);

        await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 4);
        await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
        await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 4);
        await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
        await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

        var updates = await ReadAllUpdatesAsync(session);
        var firstFinal = updates.First(x => x.Type == "final-transcript").TranscriptText!;
        var firstPromptContainingFinal = transcriber.Requests.FindIndex(x => x.Prompt?.Contains(firstFinal, StringComparison.Ordinal) == true);
        Assert.True(firstPromptContainingFinal > 0);
        Assert.All(transcriber.Requests.Take(firstPromptContainingFinal), x => Assert.DoesNotContain(firstFinal, x.Prompt ?? string.Empty));
        Assert.All(transcriber.Requests, x => Assert.Contains(options.TechnicalPrompt, x.Prompt ?? string.Empty));
    }

    private static LiveTranscriptionSession CreateSession(RecordingTranscriber transcriber, WhisperTranscriberOptions? options = null)
    {
        options ??= CreateOptions();
        options.Validate();
        return new LiveTranscriptionSession(
            "session-1",
            transcriber,
            options,
            new NoOpLog<LiveTranscriptionSession>());
    }

    private static WhisperTranscriberOptions CreateOptions()
    {
        return new WhisperTranscriberOptions
        {
            TargetSampleRate = 16000,
            PartialUpdateIntervalMs = 750,
            PartialWindowMs = 12000,
            MinimumUtteranceMs = 1800,
            EndSilenceMs = 1000,
            MaxUtteranceMs = 30000,
            PreRollMs = 500,
            PostRollMs = 700,
            VadFrameMs = 20,
            VadEnergyThreshold = 0.015,
            Language = "en",
            EnableLanguageDetection = false,
            PromptContextCharacters = 256,
            UseGpu = false,
        };
    }

    private static async Task AddChunksAsync(LiveTranscriptionSession session, float amplitude, int chunkCount)
    {
        for (var index = 0; index < chunkCount; index += 1)
        {
            await session.AddAudioChunkAsync(BuildChunk(amplitude, milliseconds: 500), CancellationToken.None);
        }
    }

    private static AudioChunk BuildChunk(float amplitude, int milliseconds)
    {
        const int sampleRate = 16000;
        var sampleCount = sampleRate * milliseconds / 1000;
        var bytes = new byte[sampleCount * sizeof(float)];
        for (var index = 0; index < sampleCount; index += 1)
        {
            BitConverter.GetBytes(amplitude).CopyTo(bytes, index * sizeof(float));
        }

        return new AudioChunk(bytes, bytes.Length, sampleRate, Channels: 1, "f32le", DateTimeOffset.UtcNow);
    }

    private static async Task<ServerEnvelope[]> ReadAllUpdatesAsync(LiveTranscriptionSession session)
    {
        var updates = new List<ServerEnvelope>();
        await foreach (var update in session.ReadUpdatesAsync(CancellationToken.None))
        {
            updates.Add(update);
        }

        return [.. updates];
    }

    private static async Task<ServerEnvelope[]> ReadSomeUpdatesAsync(LiveTranscriptionSession session, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var updates = new List<ServerEnvelope>();
        try
        {
            await foreach (var update in session.ReadUpdatesAsync(cts.Token))
            {
                updates.Add(update);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return [.. updates];
    }

    private sealed class RecordingTranscriber : IWaveTranscriber
    {
        public List<RecordedRequest> Requests { get; } = [];

        public Task WarmUpAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> TranscribeWaveAsync(WaveTranscriptionRequest request, CancellationToken cancellationToken)
        {
            var durationMs = (request.WaveBytes.Length - 44) / 2 * 1000 / 16000;
            var text = $"text-{Requests.Count + 1}";
            Requests.Add(new RecordedRequest(durationMs, request.Prompt, request.Language, request.EnableLanguageDetection));
            return Task.FromResult(text);
        }
    }

    private sealed record RecordedRequest(int DurationMs, string? Prompt, string? Language, bool EnableLanguageDetection);

    private sealed class NoOpLog<T> : ILogMachina<T>
        where T : class
    {
        public void Debug(string message)
        {
        }

        public void Error(string message)
        {
        }

        public void Error(Exception ex)
        {
        }

        public void Info(string message)
        {
        }

        public void Trace(string message)
        {
        }

        public void Warn(string message)
        {
        }
    }
}
