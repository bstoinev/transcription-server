using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Transcription;

namespace AIO.Transcription.Server.Runtime;

public sealed class LiveTranscriptionSession
{
    private readonly object sync = new();
    private readonly List<AudioChunk> utteranceChunks = [];
    private readonly IWaveTranscriber transcriber;
    private readonly WhisperTranscriberOptions options;
    private readonly ILogger<LiveTranscriptionSession> logger;
    private string lastTranscriptSegment = string.Empty;
    private bool lastTranscriptWasFinal;
    private double utteranceDurationMilliseconds;
    private double speechDurationMilliseconds;
    private double trailingSilenceMilliseconds;
    private double lastPartialTranscriptAtMilliseconds;

    public LiveTranscriptionSession(string sessionId, IWaveTranscriber transcriber, WhisperTranscriberOptions options, ILogger<LiveTranscriptionSession> logger)
    {
        SessionId = sessionId;
        this.transcriber = transcriber;
        this.options = options;
        this.logger = logger;
        Snapshot = new TranscriptionSessionSnapshot
        {
            SessionId = sessionId,
            ConnectedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public string SessionId { get; }

    public TranscriptionSessionSnapshot Snapshot { get; }

    public async Task<IReadOnlyList<ServerEnvelope>> AddAudioChunkAsync(AudioChunk chunk, bool forceFinalize, CancellationToken cancellationToken)
    {
        List<AudioChunk>? partialWindow = null;
        List<AudioChunk>? finalWindow = null;
        int receivedChunkCount;
        long receivedAudioBytes;

        lock (sync)
        {
            Snapshot.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Snapshot.ReceivedChunkCount += 1;
            Snapshot.ReceivedAudioBytes += chunk.BytesRecorded;
            Snapshot.LastEncoding = chunk.Encoding;
            Snapshot.LastSampleRate = chunk.SampleRate;
            Snapshot.LastChannels = chunk.Channels;
            receivedChunkCount = Snapshot.ReceivedChunkCount;
            receivedAudioBytes = Snapshot.ReceivedAudioBytes;

            var chunkMilliseconds = WavePcm16Writer.EstimateChunkMilliseconds(chunk);
            var hasSpeech = !options.BoundaryDetectionEnabled || WavePcm16Writer.HasSpeech(chunk, options.BoundaryDetectionRmsThreshold);

            if (!hasSpeech && speechDurationMilliseconds <= 0)
            {
                return [];
            }

            utteranceChunks.Add(chunk);
            utteranceDurationMilliseconds += chunkMilliseconds;

            if (hasSpeech)
            {
                speechDurationMilliseconds += chunkMilliseconds;
                trailingSilenceMilliseconds = 0;
            }
            else
            {
                trailingSilenceMilliseconds += chunkMilliseconds;
            }

            var shouldDiscardNoise = speechDurationMilliseconds > 0
                && speechDurationMilliseconds < options.MinimumSpeechMilliseconds
                && trailingSilenceMilliseconds >= options.BoundarySilenceMilliseconds;

            if (shouldDiscardNoise)
            {
                logger.LogDebug("Discarding short low-confidence utterance for {SessionId}", SessionId);
                ResetUtteranceStateLocked();
                return [];
            }

            var shouldFinalize = forceFinalize
                || (speechDurationMilliseconds >= options.MinimumSpeechMilliseconds
                    && trailingSilenceMilliseconds >= options.BoundarySilenceMilliseconds)
                || utteranceDurationMilliseconds >= options.MaximumSegmentMilliseconds;

            if (shouldFinalize)
            {
                finalWindow = [.. utteranceChunks];
                ResetUtteranceStateLocked();
            }
            else if (speechDurationMilliseconds >= options.MinimumSpeechMilliseconds
                && utteranceDurationMilliseconds >= options.MinimumWindowMilliseconds
                && (utteranceDurationMilliseconds - lastPartialTranscriptAtMilliseconds) >= options.MinimumWindowMilliseconds)
            {
                partialWindow = [.. utteranceChunks];
                lastPartialTranscriptAtMilliseconds = utteranceDurationMilliseconds;
            }
        }

        var envelopes = new List<ServerEnvelope>(capacity: 2);
        if (partialWindow is not null)
        {
            var partial = await BuildTranscriptEnvelopeAsync(partialWindow, isFinal: false, receivedChunkCount, receivedAudioBytes, cancellationToken);
            if (partial is not null)
            {
                envelopes.Add(partial);
            }
        }

        if (finalWindow is not null)
        {
            var final = await BuildTranscriptEnvelopeAsync(finalWindow, isFinal: true, receivedChunkCount, receivedAudioBytes, cancellationToken);
            if (final is not null)
            {
                envelopes.Add(final);
            }
        }

        return envelopes;
    }

    public async Task<IReadOnlyList<ServerEnvelope>> CompleteAsync(CancellationToken cancellationToken)
    {
        List<AudioChunk>? finalWindow = null;
        int receivedChunkCount;
        long receivedAudioBytes;

        lock (sync)
        {
            receivedChunkCount = Snapshot.ReceivedChunkCount;
            receivedAudioBytes = Snapshot.ReceivedAudioBytes;
            if (speechDurationMilliseconds >= options.MinimumSpeechMilliseconds && utteranceChunks.Count > 0)
            {
                finalWindow = [.. utteranceChunks];
            }

            ResetUtteranceStateLocked();
        }

        if (finalWindow is null)
        {
            return [];
        }

        var final = await BuildTranscriptEnvelopeAsync(finalWindow, isFinal: true, receivedChunkCount, receivedAudioBytes, cancellationToken);
        return final is null ? [] : [final];
    }

    public ServerEnvelope BuildEndedEnvelope()
    {
        return new ServerEnvelope
        {
            Type = "session-ended",
            SessionId = SessionId,
            Message = "Session ended.",
            ReceivedChunkCount = Snapshot.ReceivedChunkCount,
            ReceivedAudioBytes = Snapshot.ReceivedAudioBytes,
        };
    }

    private async Task<ServerEnvelope?> BuildTranscriptEnvelopeAsync(
        IReadOnlyList<AudioChunk> window,
        bool isFinal,
        int receivedChunkCount,
        long receivedAudioBytes,
        CancellationToken cancellationToken)
    {
        var waveBytes = WavePcm16Writer.WriteWaveFile(window, options.TargetSampleRate);
        var text = (await transcriber.TranscribeWaveAsync(waveBytes, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        lock (sync)
        {
            if (string.Equals(lastTranscriptSegment, text, StringComparison.OrdinalIgnoreCase)
                && (!isFinal || lastTranscriptWasFinal))
            {
                logger.LogDebug("Skipping duplicate {TranscriptKind} transcript segment for {SessionId}", isFinal ? "final" : "partial", SessionId);
                return null;
            }

            lastTranscriptSegment = text;
            lastTranscriptWasFinal = isFinal;
        }

        return new ServerEnvelope
        {
            Type = "transcript",
            SessionId = SessionId,
            Message = isFinal ? "Utterance finalized." : "Transcript updated.",
            TranscriptText = text,
            IsFinal = isFinal,
            ReceivedChunkCount = receivedChunkCount,
            ReceivedAudioBytes = receivedAudioBytes,
        };
    }

    private void ResetUtteranceStateLocked()
    {
        utteranceChunks.Clear();
        utteranceDurationMilliseconds = 0;
        speechDurationMilliseconds = 0;
        trailingSilenceMilliseconds = 0;
        lastPartialTranscriptAtMilliseconds = 0;
    }
}
