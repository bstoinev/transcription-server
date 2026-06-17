using System.Threading.Channels;
using System.Runtime.InteropServices;
using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Logging;
using AIO.Transcription.Server.Transcription;
using LogMachina;

namespace AIO.Transcription.Server.Runtime;

public sealed class LiveTranscriptionSession : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Channel<AudioChunk> inputChannel = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Channel<ServerEnvelope> updatesChannel = Channel.CreateUnbounded<ServerEnvelope>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = true
    });
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly IWaveTranscriber transcriber;
    private readonly WhisperTranscriberOptions options;
    private readonly IVoiceActivityDetector voiceActivityDetector;
    private readonly ILogMachina<LiveTranscriptionSession> log;
    private readonly TranscriptionSessionSnapshot snapshot;
    private readonly Task processingTask;
    private readonly List<float> rollingSamples = [];
    private readonly List<float> utteranceSamples = [];
    private readonly List<float> vadFrameSamples = [];
    private string transcriptPromptContext = string.Empty;
    private string currentUtteranceId = string.Empty;
    private string lastPartialText = string.Empty;
    private int utteranceSequence;
    private int partialSequence;
    private int lastPartialAtSampleCount;
    private int lastSpeechSampleExclusive;
    private int speechSampleCount;
    private int silenceSampleCount;
    private bool utteranceActive;
    private bool acceptingAudio = true;
    private bool flushPendingAudioOnCompletion;
    private Exception? processingFailure;
    private bool failureReported;

    public LiveTranscriptionSession(
        string sessionId,
        IWaveTranscriber transcriber,
        WhisperTranscriberOptions options,
        ILogMachina<LiveTranscriptionSession> log,
        IVoiceActivityDetector? voiceActivityDetector = null)
    {
        SessionId = sessionId;
        ModelType = WhisperModelCatalog.GetEffectiveConfiguredModelType(options);
        this.transcriber = transcriber;
        this.options = options;
        this.log = log;
        this.voiceActivityDetector = voiceActivityDetector ?? new EnergyVoiceActivityDetector(options.VadEnergyThreshold);
        snapshot = new TranscriptionSessionSnapshot
        {
            SessionId = sessionId,
            ConnectedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        processingTask = ProcessChunksAsync();
        this.log.Info($"Initialized live transcription session. SessionId={sessionId} ModelType={ModelType} Language={options.Language ?? "<auto>"}");
    }

    public string SessionId { get; }
    public string ModelType { get; }

    public async Task AddAudioChunkAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        EnsureSessionIsAcceptingAudio();
        await inputChannel.Writer.WriteAsync(chunk, cancellationToken);

        lock (sync)
        {
            if (snapshot.ReceivedChunkCount > 0 &&
                (!string.Equals(snapshot.LastEncoding, chunk.Encoding, StringComparison.OrdinalIgnoreCase) ||
                 snapshot.LastSampleRate != chunk.SampleRate ||
                 snapshot.LastChannels != chunk.Channels))
            {
                log.Warn(
                    $"Audio format changed within active session. SessionId={SessionId} PreviousEncoding={snapshot.LastEncoding} PreviousSampleRate={snapshot.LastSampleRate} PreviousChannels={snapshot.LastChannels} NewEncoding={chunk.Encoding} NewSampleRate={chunk.SampleRate} NewChannels={chunk.Channels}");
            }

            snapshot.UpdatedAtUtc = DateTimeOffset.UtcNow;
            snapshot.ReceivedChunkCount += 1;
            snapshot.ReceivedAudioBytes += chunk.BytesRecorded;
            snapshot.LastEncoding = chunk.Encoding;
            snapshot.LastSampleRate = chunk.SampleRate;
            snapshot.LastChannels = chunk.Channels;
        }
    }

    public IAsyncEnumerable<ServerEnvelope> ReadUpdatesAsync(CancellationToken cancellationToken)
    {
        return updatesChannel.Reader.ReadAllAsync(cancellationToken);
    }

    public async Task CompleteAsync(bool flushPendingAudio, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            flushPendingAudioOnCompletion |= flushPendingAudio;
            acceptingAudio = false;
        }

        inputChannel.Writer.TryComplete();
        await processingTask.WaitAsync(cancellationToken);
    }

    public ServerEnvelope BuildEndedEnvelope()
    {
        var snapshotCopy = CreateSnapshot();
        return new ServerEnvelope
        {
            Type = "session-ended",
            SessionId = SessionId,
            Message = "Session ended.",
            ModelType = ModelType,
            ReceivedChunkCount = snapshotCopy.ReceivedChunkCount,
            ReceivedAudioBytes = snapshotCopy.ReceivedAudioBytes,
        };
    }

    public TranscriptionSessionSnapshot CreateSnapshot()
    {
        lock (sync)
        {
            return CloneSnapshot(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            acceptingAudio = false;
        }

        lifetimeCts.Cancel();
        inputChannel.Writer.TryComplete();

        try
        {
            await processingTask;
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
        }
        finally
        {
            switch (transcriber)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        lifetimeCts.Dispose();
    }

    private void EnsureSessionIsAcceptingAudio()
    {
        lock (sync)
        {
            if (processingFailure is not null)
            {
                if (!failureReported)
                {
                    failureReported = true;
                    throw new InvalidOperationException($"Session {SessionId} can no longer process audio.", processingFailure);
                }

                throw new InvalidOperationException($"Session {SessionId} can no longer process audio because transcription already failed.");
            }

            if (!acceptingAudio)
            {
                throw new InvalidOperationException($"Session {SessionId} is no longer accepting audio.");
            }
        }
    }

    private async Task ProcessChunksAsync()
    {
        try
        {
            await foreach (var chunk in inputChannel.Reader.ReadAllAsync(lifetimeCts.Token))
            {
                var samples = WavePcm16Writer.DecodeMonoSamples(chunk, options.TargetSampleRate);
                await ProcessSamplesAsync(samples, lifetimeCts.Token);
            }

            if (utteranceActive && ShouldFlushPendingAudio())
            {
                await FinalizeUtteranceAsync(force: true, lifetimeCts.Token);
            }
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
            log.Info($"Stopped background transcription processing. SessionId={SessionId}");
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                processingFailure = ex;
                acceptingAudio = false;
            }

            log.Error($"Background transcription processing failed. SessionId={SessionId}", ex);
            updatesChannel.Writer.TryWrite(new ServerEnvelope
            {
                Type = "error",
                SessionId = SessionId,
                Message = $"Session transcription failed: {ex.Message}"
            });
        }
        finally
        {
            updatesChannel.Writer.TryComplete();
        }
    }

    private async Task ProcessSamplesAsync(float[] samples, CancellationToken cancellationToken)
    {
        var vadFrameSampleCount = MillisecondsToSamples(options.VadFrameMs);
        var maxUtteranceSamples = MillisecondsToSamples(options.MaxUtteranceMs);

        foreach (var sample in samples)
        {
            AppendRollingSample(sample);
            vadFrameSamples.Add(sample);

            if (utteranceActive)
            {
                utteranceSamples.Add(sample);
                if (utteranceSamples.Count >= maxUtteranceSamples)
                {
                    await FinalizeUtteranceAsync(force: true, cancellationToken);
                }
            }

            if (vadFrameSamples.Count < vadFrameSampleCount)
            {
                continue;
            }

            await ProcessVadFrameAsync(cancellationToken);
        }

        if (utteranceActive)
        {
            await MaybeEmitPartialAsync(cancellationToken);
        }
    }

    private async Task ProcessVadFrameAsync(CancellationToken cancellationToken)
    {
        var frameIsSpeech = voiceActivityDetector.IsSpeech(CollectionsMarshal.AsSpan(vadFrameSamples));
        if (frameIsSpeech)
        {
            if (!utteranceActive)
            {
                StartUtterance();
            }

            speechSampleCount += vadFrameSamples.Count;
            silenceSampleCount = 0;
            lastSpeechSampleExclusive = utteranceSamples.Count;
        }
        else if (utteranceActive)
        {
            silenceSampleCount += vadFrameSamples.Count;
            if (silenceSampleCount >= MillisecondsToSamples(options.EndSilenceMs))
            {
                await FinalizeUtteranceAsync(force: false, cancellationToken);
            }
        }

        vadFrameSamples.Clear();

        if (utteranceActive)
        {
            await MaybeEmitPartialAsync(cancellationToken);
        }
    }

    private void StartUtterance()
    {
        utteranceSequence += 1;
        partialSequence = 0;
        lastPartialAtSampleCount = 0;
        lastSpeechSampleExclusive = 0;
        speechSampleCount = 0;
        silenceSampleCount = 0;
        lastPartialText = string.Empty;
        currentUtteranceId = $"{SessionId}-{utteranceSequence:D6}";
        utteranceSamples.Clear();

        var seedSampleCount = Math.Min(rollingSamples.Count, MillisecondsToSamples(options.PreRollMs) + vadFrameSamples.Count);
        if (seedSampleCount > 0)
        {
            utteranceSamples.AddRange(rollingSamples.GetRange(rollingSamples.Count - seedSampleCount, seedSampleCount));
        }

        utteranceActive = true;
        log.Info($"Started utterance. SessionId={SessionId} UtteranceId={currentUtteranceId} SeedSamples={seedSampleCount}");
    }

    private async Task MaybeEmitPartialAsync(CancellationToken cancellationToken)
    {
        if (!utteranceActive ||
            utteranceSamples.Count - lastPartialAtSampleCount < MillisecondsToSamples(options.PartialUpdateIntervalMs))
        {
            return;
        }

        var partialSamples = TakeLastSamples(utteranceSamples, MillisecondsToSamples(options.PartialWindowMs));
        var text = await TranscribeSamplesAsync(partialSamples, cancellationToken);
        lastPartialAtSampleCount = utteranceSamples.Count;
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, lastPartialText, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lastPartialText = text;
        partialSequence += 1;
        var snapshotCopy = CreateSnapshot();
        updatesChannel.Writer.TryWrite(new ServerEnvelope
        {
            Type = "partial-transcript",
            SessionId = SessionId,
            UtteranceId = currentUtteranceId,
            Sequence = partialSequence,
            TranscriptText = text,
            ModelType = ModelType,
            ReceivedChunkCount = snapshotCopy.ReceivedChunkCount,
            ReceivedAudioBytes = snapshotCopy.ReceivedAudioBytes,
        });
    }

    private async Task FinalizeUtteranceAsync(bool force, CancellationToken cancellationToken)
    {
        if (!utteranceActive)
        {
            return;
        }

        var utteranceId = currentUtteranceId;
        var minimumSamples = MillisecondsToSamples(options.MinimumUtteranceMs);
        var finalSampleCount = Math.Min(
            utteranceSamples.Count,
            Math.Max(lastSpeechSampleExclusive, 0) + MillisecondsToSamples(options.PostRollMs));
        var finalSamples = finalSampleCount > 0
            ? utteranceSamples.GetRange(0, finalSampleCount)
            : [];
        var shouldFinalize = speechSampleCount >= minimumSamples && finalSamples.Count > 0;

        ResetUtteranceState();

        if (!shouldFinalize)
        {
            log.Info(
                $"Dropped short utterance. SessionId={SessionId} UtteranceId={utteranceId} SpeechMs={SamplesToMilliseconds(speechSampleCount):F2} Force={force}");
            return;
        }

        var text = await TranscribeSamplesAsync(finalSamples, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        AppendPromptContext(text);
        var snapshotCopy = CreateSnapshot();
        updatesChannel.Writer.TryWrite(new ServerEnvelope
        {
            Type = "final-transcript",
            SessionId = SessionId,
            UtteranceId = utteranceId,
            TranscriptText = text,
            ModelType = ModelType,
            ReceivedChunkCount = snapshotCopy.ReceivedChunkCount,
            ReceivedAudioBytes = snapshotCopy.ReceivedAudioBytes,
        });
    }

    private void ResetUtteranceState()
    {
        utteranceActive = false;
        currentUtteranceId = string.Empty;
        partialSequence = 0;
        lastPartialAtSampleCount = 0;
        lastSpeechSampleExclusive = 0;
        speechSampleCount = 0;
        silenceSampleCount = 0;
        lastPartialText = string.Empty;
        utteranceSamples.Clear();
    }

    private async Task<string> TranscribeSamplesAsync(IReadOnlyList<float> samples, CancellationToken cancellationToken)
    {
        var waveBytes = WavePcm16Writer.WriteWaveFile(samples, options.TargetSampleRate);
        var request = new WaveTranscriptionRequest(
            waveBytes,
            GetPromptContext(),
            options.Language,
            EnableLanguageDetection: false);
        return (await transcriber.TranscribeWaveAsync(request, cancellationToken)).Trim();
    }

    private string? GetPromptContext()
    {
        lock (sync)
        {
            var pieces = new[] { options.TechnicalPrompt, transcriptPromptContext }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim());
            var prompt = string.Join(Environment.NewLine, pieces);
            return string.IsNullOrWhiteSpace(prompt) ? null : prompt;
        }
    }

    private void AppendPromptContext(string text)
    {
        if (options.PromptContextCharactersResolved <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (sync)
        {
            transcriptPromptContext = string.IsNullOrWhiteSpace(transcriptPromptContext)
                ? text
                : $"{transcriptPromptContext} {text}".Trim();

            if (transcriptPromptContext.Length > options.PromptContextCharactersResolved)
            {
                transcriptPromptContext = transcriptPromptContext[^options.PromptContextCharactersResolved..].TrimStart();
            }
        }
    }

    private void AppendRollingSample(float sample)
    {
        rollingSamples.Add(sample);
        var maxRollingSamples = MillisecondsToSamples(options.PartialWindowMs + options.PreRollMs);
        if (rollingSamples.Count > maxRollingSamples)
        {
            rollingSamples.RemoveRange(0, rollingSamples.Count - maxRollingSamples);
        }
    }

    private List<float> TakeLastSamples(List<float> source, int sampleCount)
    {
        var count = Math.Min(source.Count, sampleCount);
        return count == source.Count
            ? [.. source]
            : source.GetRange(source.Count - count, count);
    }

    private int MillisecondsToSamples(int milliseconds)
    {
        return Math.Max(1, (int)Math.Round(options.TargetSampleRate * (milliseconds / 1000.0)));
    }

    private double SamplesToMilliseconds(int samples)
    {
        return samples * 1000.0 / options.TargetSampleRate;
    }

    private bool ShouldFlushPendingAudio()
    {
        lock (sync)
        {
            return flushPendingAudioOnCompletion;
        }
    }

    private static TranscriptionSessionSnapshot CloneSnapshot(TranscriptionSessionSnapshot source)
    {
        return new TranscriptionSessionSnapshot
        {
            SessionId = source.SessionId,
            ConnectedAtUtc = source.ConnectedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            ReceivedChunkCount = source.ReceivedChunkCount,
            ReceivedAudioBytes = source.ReceivedAudioBytes,
            LastEncoding = source.LastEncoding,
            LastSampleRate = source.LastSampleRate,
            LastChannels = source.LastChannels,
        };
    }
}
