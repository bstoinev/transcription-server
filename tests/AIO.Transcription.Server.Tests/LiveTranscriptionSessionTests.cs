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
    public async Task DiagnosticsDisabledProducesNoDebugWavFiles()
    {
        var directory = CreateTemporaryDiagnosticsDirectory();
        try
        {
            var transcriber = new RecordingTranscriber();
            var options = CreateOptions();
            options.MinimumUtteranceMs = 500;
            options.EnableLiveDiagnostics = false;
            options.SaveDebugUtteranceWavFiles = true;
            options.DebugUtteranceDirectory = directory;
            await using var session = CreateSession(transcriber, options);

            await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 4);
            await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
            await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

            Assert.Empty(GetDebugWavFiles(directory));
        }
        finally
        {
            DeleteTemporaryPath(directory);
        }
    }

    [Fact]
    public async Task DroppedUtteranceCanProduceDebugWavWithBelowMinimumReason()
    {
        var directory = CreateTemporaryDiagnosticsDirectory();
        try
        {
            var transcriber = new RecordingTranscriber();
            var options = CreateDiagnosticsOptions(directory);
            await using var session = CreateSession(transcriber, options);

            await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 2);
            await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
            await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

            var updates = await ReadAllUpdatesAsync(session);
            Assert.DoesNotContain(updates, x => x.Type == "final-transcript");
            var wavPath = Assert.Single(GetDebugWavFiles(directory));
            AssertWaveFile(wavPath);
            using var metadata = ReadSingleDebugMetadata(directory);
            Assert.Equal("dropped", metadata.RootElement.GetProperty("status").GetString());
            Assert.Equal("BelowMinimumUtteranceMs", metadata.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            DeleteTemporaryPath(directory);
        }
    }

    [Fact]
    public async Task FinalizedUtteranceCanProduceDebugWav()
    {
        var directory = CreateTemporaryDiagnosticsDirectory();
        try
        {
            var transcriber = new RecordingTranscriber();
            var options = CreateDiagnosticsOptions(directory);
            options.MinimumUtteranceMs = 500;
            await using var session = CreateSession(transcriber, options);

            await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 4);
            await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
            await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

            var updates = await ReadAllUpdatesAsync(session);
            Assert.Single(updates, x => x.Type == "final-transcript");
            var wavPath = Assert.Single(GetDebugWavFiles(directory));
            AssertWaveFile(wavPath);
            using var metadata = ReadSingleDebugMetadata(directory);
            Assert.Equal("finalized", metadata.RootElement.GetProperty("status").GetString());
            Assert.Equal("EndSilenceMs", metadata.RootElement.GetProperty("reason").GetString());
            Assert.StartsWith("text-", metadata.RootElement.GetProperty("transcriptText").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryPath(directory);
        }
    }

    [Fact]
    public async Task MaxDebugUtteranceFilesPerSessionIsRespected()
    {
        var directory = CreateTemporaryDiagnosticsDirectory();
        try
        {
            var transcriber = new RecordingTranscriber();
            var options = CreateDiagnosticsOptions(directory);
            options.MaxDebugUtteranceFilesPerSession = 1;
            await using var session = CreateSession(transcriber, options);

            await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 2);
            await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
            await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 2);
            await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
            await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

            Assert.Single(GetDebugWavFiles(directory));
        }
        finally
        {
            DeleteTemporaryPath(directory);
        }
    }

    [Fact]
    public async Task EmptyWhisperResultIsRecordedDistinctlyFromBelowMinimumUtterance()
    {
        var directory = CreateTemporaryDiagnosticsDirectory();
        try
        {
            var transcriber = new RecordingTranscriber(_ => string.Empty);
            var options = CreateDiagnosticsOptions(directory);
            options.MinimumUtteranceMs = 500;
            await using var session = CreateSession(transcriber, options);

            await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 4);
            await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
            await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

            var updates = await ReadAllUpdatesAsync(session);
            Assert.DoesNotContain(updates, x => x.Type == "final-transcript");
            using var metadata = ReadSingleDebugMetadata(directory);
            Assert.Equal("dropped", metadata.RootElement.GetProperty("status").GetString());
            Assert.Equal("EmptyWhisperResult", metadata.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            DeleteTemporaryPath(directory);
        }
    }

    [Fact]
    public async Task DebugWavSaveFailureDoesNotCrashSession()
    {
        var directory = CreateTemporaryDiagnosticsDirectory();
        try
        {
            var badDebugDirectory = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(badDebugDirectory, "occupied");
            var transcriber = new RecordingTranscriber();
            var options = CreateDiagnosticsOptions(badDebugDirectory);
            options.MinimumUtteranceMs = 500;
            await using var session = CreateSession(transcriber, options);

            await AddChunksAsync(session, amplitude: 0.1f, chunkCount: 4);
            await AddChunksAsync(session, amplitude: 0.0f, chunkCount: 2);
            await session.CompleteAsync(flushPendingAudio: true, CancellationToken.None);

            var updates = await ReadAllUpdatesAsync(session);
            Assert.Single(updates, x => x.Type == "final-transcript");
        }
        finally
        {
            DeleteTemporaryPath(directory);
        }
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

    [Fact]
    public void StartSessionRequiresModelTypeWhenServerIsNotPinned()
    {
        var options = CreateOptions();
        var error = Assert.Throws<InvalidOperationException>(() =>
            WhisperModelCatalog.CreateSessionOptions(options, requestedModelType: null, requestedPrompt: null, requestedLanguage: null, requestedEnableLanguageDetection: null));
        Assert.Contains("requires modelType", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilitiesDoNotAdvertiseDefaultModelWhenSessionMustChoose()
    {
        var capabilities = WhisperModelCatalog.BuildCapabilities(CreateOptions());
        Assert.Equal(string.Empty, capabilities.DefaultModelType);
        Assert.True(capabilities.SupportsSessionModelSelection);
    }

    [Fact]
    public void SessionPromptIsAppendedToConfiguredTechnicalPrompt()
    {
        var options = CreateOptions();
        options.TechnicalPrompt = "server prompt";

        var sessionOptions = WhisperModelCatalog.CreateSessionOptions(
            options,
            requestedModelType: "medium.en",
            requestedPrompt: "client prompt",
            requestedLanguage: null,
            requestedEnableLanguageDetection: null);

        Assert.Equal("server prompt" + Environment.NewLine + "client prompt", sessionOptions.TechnicalPrompt);
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
            ModelType = "medium.en",
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

    private static WhisperTranscriberOptions CreateDiagnosticsOptions(string directory)
    {
        var options = CreateOptions();
        options.EnableLiveDiagnostics = true;
        options.SaveDebugUtteranceWavFiles = true;
        options.DebugUtteranceDirectory = directory;
        return options;
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

    private static string CreateTemporaryDiagnosticsDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aio-transcription-diagnostics-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryPath(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string[] GetDebugWavFiles(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.wav", SearchOption.AllDirectories)
            : [];
    }

    private static JsonDocument ReadSingleDebugMetadata(string directory)
    {
        var jsonPath = Assert.Single(Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories));
        return JsonDocument.Parse(File.ReadAllText(jsonPath));
    }

    private static void AssertWaveFile(string wavPath)
    {
        var bytes = File.ReadAllBytes(wavPath);
        Assert.True(bytes.Length > 44);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
    }

    private sealed class RecordingTranscriber : IWaveTranscriber
    {
        private readonly Func<int, string> responseFactory;

        public RecordingTranscriber(Func<int, string>? responseFactory = null)
        {
            this.responseFactory = responseFactory ?? (requestNumber => $"text-{requestNumber}");
        }

        public List<RecordedRequest> Requests { get; } = [];

        public Task WarmUpAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> TranscribeWaveAsync(WaveTranscriptionRequest request, CancellationToken cancellationToken)
        {
            var durationMs = (request.WaveBytes.Length - 44) / 2 * 1000 / 16000;
            var text = responseFactory(Requests.Count + 1);
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
