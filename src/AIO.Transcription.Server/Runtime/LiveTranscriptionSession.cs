using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using System.Text.Json;
using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Logging;
using AIO.Transcription.Server.Transcription;
using LogMachina;

namespace AIO.Transcription.Server.Runtime;

public sealed class LiveTranscriptionSession : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Queue<AudioChunk> inputQueue = new();
    private readonly Channel<bool> inputQueueSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
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
    private int samplesSinceLastSpeech;
    private double maxObservedRmsSinceLastSpeech;
    private double currentUtterancePeakRms;
    private bool utteranceActive;
    private bool acceptingAudio = true;
    private bool inputQueueCompleted;
    private bool flushPendingAudioOnCompletion;
    private Exception? processingFailure;
    private bool failureReported;
    private double queuedAudioDurationMs;
    private long queuedAudioBytes;
    private long droppedQueuedAudioChunkCount;
    private long droppedQueuedAudioBytes;
    private double droppedQueuedAudioDurationMs;
    private long totalNormalizedSampleCount;
    private int utterancesStarted;
    private int utterancesFinalized;
    private int utterancesDropped;
    private int partialTranscriptsRequested;
    private int finalTranscriptsRequested;
    private int finalTranscriptsEmitted;
    private int debugUtteranceFilesWritten;
    private bool sessionSummaryLogged;
    private readonly Dictionary<string, int> dropCountsByReason = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<double> completedUtteranceDurationsMs = [];

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

    public Task AddAudioChunkAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();

        AudioQueueDropSummary? dropSummary;
        lock (sync)
        {
            EnsureSessionIsAcceptingAudioUnderLock();
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
            dropSummary = EnqueueLiveAudioChunkUnderLock(chunk);
        }

        inputQueueSignal.Writer.TryWrite(true);
        LogQueuedAudioDrop(dropSummary);
        return Task.CompletedTask;
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
            inputQueueCompleted = true;
        }

        inputQueueSignal.Writer.TryWrite(true);
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
            inputQueueCompleted = true;
        }

        lifetimeCts.Cancel();
        inputQueueSignal.Writer.TryWrite(true);

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

    private void EnsureSessionIsAcceptingAudioUnderLock()
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

    private async Task ProcessChunksAsync()
    {
        try
        {
            while (await ReadNextQueuedAudioChunkAsync(lifetimeCts.Token) is { } chunk)
            {
                var samples = WavePcm16Writer.DecodeMonoSamples(chunk, options.TargetSampleRate);
                var chunkContainedSpeechFrame = await ProcessSamplesAsync(samples, lifetimeCts.Token);
                LogAudioChunkDiagnostics(chunk, samples, chunkContainedSpeechFrame);
            }

            if (utteranceActive && ShouldFlushPendingAudio())
            {
                await FinalizeUtteranceAsync("EndSessionFlush", lifetimeCts.Token);
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
            LogSessionSummary();
            updatesChannel.Writer.TryComplete();
        }
    }

    private async Task<AudioChunk?> ReadNextQueuedAudioChunkAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (sync)
            {
                if (inputQueue.Count > 0)
                {
                    var chunk = inputQueue.Dequeue();
                    var chunkDurationMs = EstimateQueuedChunkMilliseconds(chunk);
                    queuedAudioDurationMs = Math.Max(0, queuedAudioDurationMs - chunkDurationMs);
                    queuedAudioBytes = Math.Max(0, queuedAudioBytes - chunk.BytesRecorded);
                    return chunk;
                }

                if (inputQueueCompleted)
                {
                    return null;
                }
            }

            await inputQueueSignal.Reader.ReadAsync(cancellationToken);
        }
    }

    private AudioQueueDropSummary? EnqueueLiveAudioChunkUnderLock(AudioChunk chunk)
    {
        var chunkDurationMs = EstimateQueuedChunkMilliseconds(chunk);
        inputQueue.Enqueue(chunk);
        queuedAudioDurationMs += chunkDurationMs;
        queuedAudioBytes += chunk.BytesRecorded;

        var droppedChunkCount = 0;
        var droppedBytes = 0L;
        var droppedDurationMs = 0.0;
        while (inputQueue.Count > 1 && queuedAudioDurationMs > options.MaxQueuedAudioBufferMs)
        {
            var droppedChunk = inputQueue.Dequeue();
            var droppedChunkDurationMs = EstimateQueuedChunkMilliseconds(droppedChunk);
            queuedAudioDurationMs = Math.Max(0, queuedAudioDurationMs - droppedChunkDurationMs);
            queuedAudioBytes = Math.Max(0, queuedAudioBytes - droppedChunk.BytesRecorded);
            droppedChunkCount += 1;
            droppedBytes += droppedChunk.BytesRecorded;
            droppedDurationMs += droppedChunkDurationMs;
        }

        if (droppedChunkCount == 0)
        {
            return null;
        }

        droppedQueuedAudioChunkCount += droppedChunkCount;
        droppedQueuedAudioBytes += droppedBytes;
        droppedQueuedAudioDurationMs += droppedDurationMs;
        return new AudioQueueDropSummary(
            droppedChunkCount,
            droppedBytes,
            droppedDurationMs,
            inputQueue.Count,
            queuedAudioBytes,
            queuedAudioDurationMs);
    }

    private async Task<bool> ProcessSamplesAsync(float[] samples, CancellationToken cancellationToken)
    {
        var vadFrameSampleCount = MillisecondsToSamples(options.VadFrameMs);
        var maxUtteranceSamples = MillisecondsToSamples(options.MaxUtteranceMs);
        var chunkContainedSpeechFrame = false;

        foreach (var sample in samples)
        {
            AppendRollingSample(sample);
            totalNormalizedSampleCount += 1;
            vadFrameSamples.Add(sample);

            if (utteranceActive)
            {
                utteranceSamples.Add(sample);
                if (utteranceSamples.Count >= maxUtteranceSamples)
                {
                    await FinalizeUtteranceAsync("MaxUtteranceMs", cancellationToken);
                }
            }

            if (vadFrameSamples.Count < vadFrameSampleCount)
            {
                continue;
            }

            var frameStartSample = totalNormalizedSampleCount - vadFrameSamples.Count;
            chunkContainedSpeechFrame |= await ProcessVadFrameAsync(frameStartSample, cancellationToken);
        }

        if (utteranceActive)
        {
            await MaybeEmitPartialAsync(cancellationToken);
        }

        return chunkContainedSpeechFrame;
    }

    private async Task<bool> ProcessVadFrameAsync(long frameStartSample, CancellationToken cancellationToken)
    {
        var frameSamples = CollectionsMarshal.AsSpan(vadFrameSamples);
        var frameRms = CalculateRms(frameSamples);
        var activeBeforeFrame = utteranceActive;
        var utteranceIdBeforeFrame = currentUtteranceId;
        maxObservedRmsSinceLastSpeech = Math.Max(maxObservedRmsSinceLastSpeech, frameRms);
        var frameIsSpeech = voiceActivityDetector.IsSpeech(frameSamples);
        if (frameIsSpeech)
        {
            if (!utteranceActive)
            {
                StartUtterance(frameRms, CalculatePeakAbsolute(frameSamples));
            }

            currentUtterancePeakRms = Math.Max(currentUtterancePeakRms, frameRms);
            speechSampleCount += vadFrameSamples.Count;
            silenceSampleCount = 0;
            samplesSinceLastSpeech = 0;
            maxObservedRmsSinceLastSpeech = 0;
            lastSpeechSampleExclusive = utteranceSamples.Count;
        }
        else if (utteranceActive)
        {
            currentUtterancePeakRms = Math.Max(currentUtterancePeakRms, frameRms);
            silenceSampleCount += vadFrameSamples.Count;
            if (silenceSampleCount >= MillisecondsToSamples(options.EndSilenceMs))
            {
                await FinalizeUtteranceAsync("EndSilenceMs", cancellationToken);
            }
        }
        else
        {
            samplesSinceLastSpeech += vadFrameSamples.Count;
            if (samplesSinceLastSpeech >= MillisecondsToSamples(10000))
            {
                log.Warn(
                    $"No speech detected yet for active session audio. SessionId={SessionId} ObservedMs={SamplesToMilliseconds(samplesSinceLastSpeech):F0} MaxObservedRms={maxObservedRmsSinceLastSpeech:F4} VadThreshold={options.VadEnergyThreshold:F4}{BuildNearThresholdHint(maxObservedRmsSinceLastSpeech)}");
                samplesSinceLastSpeech = 0;
                maxObservedRmsSinceLastSpeech = 0;
            }
        }

        LogVadFrameDiagnostics(
            utteranceIdBeforeFrame,
            frameStartSample,
            vadFrameSamples.Count,
            frameRms,
            frameIsSpeech,
            activeBeforeFrame,
            utteranceActive);
        vadFrameSamples.Clear();

        if (utteranceActive)
        {
            await MaybeEmitPartialAsync(cancellationToken);
        }

        return frameIsSpeech;
    }

    private void StartUtterance(double startFrameRms, double startFramePeak)
    {
        utteranceSequence += 1;
        partialSequence = 0;
        lastPartialAtSampleCount = 0;
        lastSpeechSampleExclusive = 0;
        speechSampleCount = 0;
        silenceSampleCount = 0;
        lastPartialText = string.Empty;
        currentUtterancePeakRms = startFrameRms;
        currentUtteranceId = $"{SessionId}-{utteranceSequence:D6}";
        utteranceSamples.Clear();

        var seedSampleCount = Math.Min(rollingSamples.Count, MillisecondsToSamples(options.PreRollMs) + vadFrameSamples.Count);
        if (seedSampleCount > 0)
        {
            utteranceSamples.AddRange(rollingSamples.GetRange(rollingSamples.Count - seedSampleCount, seedSampleCount));
        }

        utteranceActive = true;
        samplesSinceLastSpeech = 0;
        maxObservedRmsSinceLastSpeech = 0;
        utterancesStarted += 1;
        log.Info($"Started utterance. SessionId={SessionId} UtteranceId={currentUtteranceId} SeedSamples={seedSampleCount}");
        LogUtteranceDiagnostics(
            $"SpeechStartDetected sessionId={SessionId} utteranceId={currentUtteranceId} preRollMs={options.PreRollMs} rollingBufferDurationMs={SamplesToMilliseconds(rollingSamples.Count):F2} startFrameRms={startFrameRms:F6} startFramePeak={startFramePeak:F6}");
    }

    private async Task MaybeEmitPartialAsync(CancellationToken cancellationToken)
    {
        if (!utteranceActive ||
            utteranceSamples.Count - lastPartialAtSampleCount < MillisecondsToSamples(options.PartialUpdateIntervalMs))
        {
            return;
        }

        var partialSamples = TakeLastSamples(utteranceSamples, MillisecondsToSamples(options.PartialWindowMs));
        var requestSequence = partialSequence + 1;
        partialTranscriptsRequested += 1;
        LogUtteranceDiagnostics(
            $"PartialTranscriptRequested sessionId={SessionId} utteranceId={currentUtteranceId} sequence={requestSequence} audioDurationMs={SamplesToMilliseconds(partialSamples.Count):F2} partialWindowMs={options.PartialWindowMs} reason=scheduled-partial");
        var stopwatch = Stopwatch.StartNew();
        var text = await TranscribeSamplesAsync(partialSamples, cancellationToken);
        stopwatch.Stop();
        LogUtteranceDiagnostics(
            $"PartialTranscriptReturned sessionId={SessionId} utteranceId={currentUtteranceId} sequence={requestSequence} textLength={text.Length} textPreview=\"{BuildTranscriptPreview(text)}\" elapsedMs={stopwatch.ElapsedMilliseconds}");
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

    private async Task FinalizeUtteranceAsync(string endReason, CancellationToken cancellationToken)
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
        var totalDurationMs = SamplesToMilliseconds(utteranceSamples.Count);
        var detectedSpeechDurationMs = SamplesToMilliseconds(speechSampleCount);
        var detectedSpeechSampleCount = speechSampleCount;
        var trailingSilenceMs = SamplesToMilliseconds(silenceSampleCount);
        var averageRms = CalculateRms(CollectionsMarshal.AsSpan(utteranceSamples));
        var peakRms = currentUtterancePeakRms;
        var utteranceSamplesForDebug = utteranceSamples.ToArray();

        LogUtteranceDiagnostics(
            $"UtteranceEndpointDetected sessionId={SessionId} utteranceId={utteranceId} totalDurationMs={totalDurationMs:F2} detectedSpeechDurationMs={detectedSpeechDurationMs:F2} trailingSilenceMs={trailingSilenceMs:F2} endReason={endReason}");

        ResetUtteranceState();

        if (!shouldFinalize)
        {
            var dropReason = DetermineDropReason(finalSamples.Count, detectedSpeechSampleCount, minimumSamples);
            var savedDebugPath = SaveDebugUtterance(
                utteranceId,
                status: "dropped",
                reason: dropReason,
                samples: finalSamples.Count > 0 ? finalSamples : utteranceSamplesForDebug,
                totalDurationMs,
                detectedSpeechDurationMs,
                averageRms,
                peakRms,
                transcriptText: null);
            RecordDroppedUtterance(dropReason, totalDurationMs);
            log.Info(
                $"Dropped utterance. SessionId={SessionId} UtteranceId={utteranceId} Reason={dropReason} SpeechMs={detectedSpeechDurationMs:F2}");
            LogUtteranceDiagnostics(
                $"UtteranceDropped sessionId={SessionId} utteranceId={utteranceId} reason={dropReason} totalDurationMs={totalDurationMs:F2} detectedSpeechDurationMs={detectedSpeechDurationMs:F2} minimumUtteranceMs={options.MinimumUtteranceMs} averageRms={averageRms:F6} peakRms={peakRms:F6} savedDebugWavPath={savedDebugPath ?? string.Empty}");
            return;
        }

        finalTranscriptsRequested += 1;
        LogUtteranceDiagnostics(
            $"FinalTranscriptRequested sessionId={SessionId} utteranceId={utteranceId} finalAudioDurationMs={SamplesToMilliseconds(finalSamples.Count):F2} preRollIncludedMs={options.PreRollMs} postRollIncludedMs={options.PostRollMs} endReason={endReason}");
        var stopwatch = Stopwatch.StartNew();
        var text = await TranscribeSamplesAsync(finalSamples, cancellationToken);
        stopwatch.Stop();
        LogUtteranceDiagnostics(
            $"FinalTranscriptReturned sessionId={SessionId} utteranceId={utteranceId} textLength={text.Length} textPreview=\"{BuildTranscriptPreview(text)}\" elapsedMs={stopwatch.ElapsedMilliseconds}");
        if (string.IsNullOrWhiteSpace(text))
        {
            var dropReason = "EmptyWhisperResult";
            var savedDebugPath = SaveDebugUtterance(
                utteranceId,
                status: "dropped",
                reason: dropReason,
                samples: finalSamples,
                totalDurationMs,
                detectedSpeechDurationMs,
                averageRms,
                peakRms,
                transcriptText: text);
            RecordDroppedUtterance(dropReason, totalDurationMs);
            LogUtteranceDiagnostics(
                $"UtteranceDropped sessionId={SessionId} utteranceId={utteranceId} reason={dropReason} totalDurationMs={totalDurationMs:F2} detectedSpeechDurationMs={detectedSpeechDurationMs:F2} minimumUtteranceMs={options.MinimumUtteranceMs} averageRms={averageRms:F6} peakRms={peakRms:F6} savedDebugWavPath={savedDebugPath ?? string.Empty}");
            return;
        }

        AppendPromptContext(text);
        var snapshotCopy = CreateSnapshot();
        var emitted = updatesChannel.Writer.TryWrite(new ServerEnvelope
        {
            Type = "final-transcript",
            SessionId = SessionId,
            UtteranceId = utteranceId,
            TranscriptText = text,
            ModelType = ModelType,
            ReceivedChunkCount = snapshotCopy.ReceivedChunkCount,
            ReceivedAudioBytes = snapshotCopy.ReceivedAudioBytes,
        });
        utterancesFinalized += 1;
        completedUtteranceDurationsMs.Add(totalDurationMs);
        SaveDebugUtterance(
            utteranceId,
            status: "finalized",
            reason: endReason,
            samples: finalSamples,
            totalDurationMs,
            detectedSpeechDurationMs,
            averageRms,
            peakRms,
            transcriptText: text);
        if (emitted)
        {
            finalTranscriptsEmitted += 1;
            LogUtteranceDiagnostics(
                $"FinalTranscriptEmitted sessionId={SessionId} utteranceId={utteranceId} textLength={text.Length} eventType=final-transcript receivedChunkCount={snapshotCopy.ReceivedChunkCount} receivedAudioBytes={snapshotCopy.ReceivedAudioBytes}");
        }
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
        currentUtterancePeakRms = 0;
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

    private double SamplesToMilliseconds(long samples)
    {
        return samples * 1000.0 / options.TargetSampleRate;
    }

    private static double EstimateQueuedChunkMilliseconds(AudioChunk chunk)
    {
        try
        {
            return WavePcm16Writer.EstimateChunkMilliseconds(chunk);
        }
        catch
        {
            return 0;
        }
    }

    private static double CalculateRms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var squareSum = 0.0;
        for (var index = 0; index < samples.Length; index += 1)
        {
            squareSum += samples[index] * samples[index];
        }

        return Math.Sqrt(squareSum / samples.Length);
    }

    private static double CalculatePeakAbsolute(ReadOnlySpan<float> samples)
    {
        var peak = 0.0;
        for (var index = 0; index < samples.Length; index += 1)
        {
            peak = Math.Max(peak, Math.Abs(samples[index]));
        }

        return peak;
    }

    private string BuildNearThresholdHint(double observedRms)
    {
        return observedRms >= options.VadEnergyThreshold * 0.8
            ? " NearThreshold=true Hint=Audio energy is close to the VAD threshold; lower Transcription:VadEnergyThreshold if speech should open an utterance."
            : string.Empty;
    }

    private void LogAudioChunkDiagnostics(AudioChunk chunk, float[] samples, bool chunkContainedSpeechFrame)
    {
        if (!options.EnableLiveDiagnostics || !options.LogAudioChunkDiagnostics)
        {
            return;
        }

        var snapshotCopy = CreateSnapshot();
        var queueSnapshot = GetInputQueueSnapshot();
        log.Info(
            $"AudioChunkDiagnostics sessionId={SessionId} chunkIndex={snapshotCopy.ReceivedChunkCount} receivedChunkCount={snapshotCopy.ReceivedChunkCount} rawByteLength={chunk.BytesRecorded} normalizedSampleCount={samples.Length} normalizedDurationMs={SamplesToMilliseconds(samples.Length):F2} sampleRate={options.TargetSampleRate} rms={CalculateRms(samples):F6} peakAbsolute={CalculatePeakAbsolute(samples):F6} vadSpeechFrameDetected={chunkContainedSpeechFrame} rollingBufferDurationMs={SamplesToMilliseconds(rollingSamples.Count):F2} utteranceState={FormatUtteranceState(utteranceActive)} queuedAudioDurationMs={queueSnapshot.DurationMs:F2} queuedAudioChunkCount={queueSnapshot.ChunkCount} droppedQueuedAudioChunks={queueSnapshot.DroppedChunkCount} droppedQueuedAudioDurationMs={queueSnapshot.DroppedDurationMs:F2}");
    }

    private void LogQueuedAudioDrop(AudioQueueDropSummary? dropSummary)
    {
        if (dropSummary is null)
        {
            return;
        }

        log.Warn(
            $"Dropped obsolete queued live audio. SessionId={SessionId} DroppedQueuedAudioChunks={dropSummary.DroppedChunkCount} DroppedQueuedAudioBytes={dropSummary.DroppedBytes} DroppedQueuedAudioDurationMs={dropSummary.DroppedDurationMs:F2} RemainingQueuedAudioChunks={dropSummary.RemainingChunkCount} RemainingQueuedAudioBytes={dropSummary.RemainingBytes} RemainingQueuedAudioDurationMs={dropSummary.RemainingDurationMs:F2} MaxQueuedAudioBufferMs={options.MaxQueuedAudioBufferMs}");
    }

    private void LogVadFrameDiagnostics(
        string utteranceIdBeforeFrame,
        long frameStartSample,
        int frameSampleCount,
        double frameRms,
        bool isSpeech,
        bool activeBeforeFrame,
        bool activeAfterFrame)
    {
        if (!options.EnableLiveDiagnostics || !options.LogVadFrameDiagnostics)
        {
            return;
        }

        var utteranceId = string.IsNullOrWhiteSpace(utteranceIdBeforeFrame)
            ? currentUtteranceId
            : utteranceIdBeforeFrame;
        log.Info(
            $"VadFrameDiagnostics sessionId={SessionId} utteranceId={utteranceId} frameStartMs={SamplesToMilliseconds(frameStartSample):F2} frameDurationMs={SamplesToMilliseconds(frameSampleCount):F2} frameRms={frameRms:F6} threshold={options.VadEnergyThreshold:F6} isSpeech={isSpeech} stateBefore={FormatUtteranceState(activeBeforeFrame)} stateAfter={FormatUtteranceState(activeAfterFrame)}");
    }

    private void LogUtteranceDiagnostics(string message)
    {
        if (!options.EnableLiveDiagnostics || !options.LogUtteranceDiagnostics)
        {
            return;
        }

        log.Info(message);
    }

    private string? SaveDebugUtterance(
        string utteranceId,
        string status,
        string reason,
        IReadOnlyList<float> samples,
        double totalDurationMs,
        double detectedSpeechDurationMs,
        double averageRms,
        double peakRms,
        string? transcriptText)
    {
        if (!options.EnableLiveDiagnostics ||
            !options.SaveDebugUtteranceWavFiles ||
            options.MaxDebugUtteranceFilesPerSession <= 0 ||
            debugUtteranceFilesWritten >= options.MaxDebugUtteranceFilesPerSession)
        {
            return null;
        }

        if (string.Equals(status, "dropped", StringComparison.OrdinalIgnoreCase) && !options.SaveDroppedUtterances)
        {
            return null;
        }

        if (string.Equals(status, "finalized", StringComparison.OrdinalIgnoreCase) && !options.SaveFinalizedUtterances)
        {
            return null;
        }

        try
        {
            var sessionDirectory = Path.Combine(options.DebugUtteranceDirectory, SanitizePathSegment(SessionId));
            Directory.CreateDirectory(sessionDirectory);

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var baseName = string.Join(
                "-",
                timestamp,
                SanitizePathSegment(utteranceId),
                SanitizePathSegment(status),
                SanitizePathSegment(reason));
            var wavPath = Path.Combine(sessionDirectory, $"{baseName}.wav");
            var jsonPath = Path.Combine(sessionDirectory, $"{baseName}.json");
            var waveBytes = WavePcm16Writer.WriteWaveFile(samples, options.TargetSampleRate);
            File.WriteAllBytes(wavPath, waveBytes);
            debugUtteranceFilesWritten += 1;

            try
            {
                var metadata = new
                {
                    sessionId = SessionId,
                    utteranceId,
                    status,
                    reason,
                    totalDurationMs,
                    detectedSpeechDurationMs,
                    averageRms,
                    peakRms,
                    vadEnergyThreshold = options.VadEnergyThreshold,
                    minimumUtteranceMs = options.MinimumUtteranceMs,
                    endSilenceMs = options.EndSilenceMs,
                    preRollMs = options.PreRollMs,
                    postRollMs = options.PostRollMs,
                    sampleRate = options.TargetSampleRate,
                    transcriptText,
                    transcriptTextLength = transcriptText?.Length ?? 0,
                    createdAtUtc = DateTimeOffset.UtcNow
                };
                var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(jsonPath, json);
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to write debug utterance metadata. SessionId={SessionId} UtteranceId={utteranceId} Path={jsonPath} ExceptionType={ex.GetType().Name} Message={ex.Message}");
            }

            return Path.GetFullPath(wavPath);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to write debug utterance WAV. SessionId={SessionId} UtteranceId={utteranceId} Status={status} Reason={reason} Directory={options.DebugUtteranceDirectory} ExceptionType={ex.GetType().Name} Message={ex.Message}");
            return null;
        }
    }

    private void RecordDroppedUtterance(string reason, double totalDurationMs)
    {
        utterancesDropped += 1;
        completedUtteranceDurationsMs.Add(totalDurationMs);
        dropCountsByReason.TryGetValue(reason, out var existingCount);
        dropCountsByReason[reason] = existingCount + 1;
    }

    private static string DetermineDropReason(int finalSampleCount, int detectedSpeechSampleCount, int minimumSampleCount)
    {
        if (finalSampleCount <= 0)
        {
            return "EmptyAudio";
        }

        if (detectedSpeechSampleCount <= 0)
        {
            return "NoSpeechDetected";
        }

        if (detectedSpeechSampleCount < minimumSampleCount)
        {
            return "BelowMinimumUtteranceMs";
        }

        return "Other";
    }

    private void LogSessionSummary()
    {
        if (!options.EnableLiveDiagnostics || sessionSummaryLogged)
        {
            return;
        }

        sessionSummaryLogged = true;
        var snapshotCopy = CreateSnapshot();
        var averageUtteranceDurationMs = completedUtteranceDurationsMs.Count == 0 ? 0 : completedUtteranceDurationsMs.Average();
        var minUtteranceDurationMs = completedUtteranceDurationsMs.Count == 0 ? 0 : completedUtteranceDurationsMs.Min();
        var maxUtteranceDurationMs = completedUtteranceDurationsMs.Count == 0 ? 0 : completedUtteranceDurationsMs.Max();
        log.Info(
            $"LiveDiagnosticsSummary sessionId={SessionId} receivedChunkCount={snapshotCopy.ReceivedChunkCount} receivedAudioBytes={snapshotCopy.ReceivedAudioBytes} totalNormalizedAudioDurationMs={SamplesToMilliseconds(totalNormalizedSampleCount):F2} utterancesStarted={utterancesStarted} utterancesFinalized={utterancesFinalized} utterancesDropped={utterancesDropped} partialTranscriptsRequested={partialTranscriptsRequested} finalTranscriptsRequested={finalTranscriptsRequested} finalTranscriptsEmitted={finalTranscriptsEmitted} dropCountsByReason={FormatDropCounts()} averageUtteranceDurationMs={averageUtteranceDurationMs:F2} minUtteranceDurationMs={minUtteranceDurationMs:F2} maxUtteranceDurationMs={maxUtteranceDurationMs:F2} droppedQueuedAudioChunks={droppedQueuedAudioChunkCount} droppedQueuedAudioBytes={droppedQueuedAudioBytes} droppedQueuedAudioDurationMs={droppedQueuedAudioDurationMs:F2} debugWavFilesWritten={debugUtteranceFilesWritten}");
    }

    private string FormatDropCounts()
    {
        if (dropCountsByReason.Count == 0)
        {
            return "none";
        }

        return string.Join(";", dropCountsByReason.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}:{x.Value}"));
    }

    private static string FormatUtteranceState(bool active)
    {
        return active ? "active" : "idle";
    }

    private static string BuildTranscriptPreview(string text)
    {
        const int MaxPreviewLength = 120;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= MaxPreviewLength
            ? normalized
            : normalized[..MaxPreviewLength];
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private AudioQueueSnapshot GetInputQueueSnapshot()
    {
        lock (sync)
        {
            return new AudioQueueSnapshot(
                inputQueue.Count,
                queuedAudioBytes,
                queuedAudioDurationMs,
                droppedQueuedAudioChunkCount,
                droppedQueuedAudioBytes,
                droppedQueuedAudioDurationMs);
        }
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

    private sealed record AudioQueueDropSummary(
        int DroppedChunkCount,
        long DroppedBytes,
        double DroppedDurationMs,
        int RemainingChunkCount,
        long RemainingBytes,
        double RemainingDurationMs);

    private sealed record AudioQueueSnapshot(
        int ChunkCount,
        long Bytes,
        double DurationMs,
        long DroppedChunkCount,
        long DroppedBytes,
        double DroppedDurationMs);
}
