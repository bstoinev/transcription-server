using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Transcription;

namespace AIO.Transcription.Server.Runtime;

public sealed class LiveTranscriptionSession
{
    private readonly object sync = new();
    private readonly List<AudioChunk> pendingChunks = [];
    private readonly IWaveTranscriber transcriber;
    private readonly WhisperTranscriberOptions options;
    private readonly ILogger<LiveTranscriptionSession> logger;
    private string lastTranscriptSegment = string.Empty;

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

    public async Task<ServerEnvelope?> AddAudioChunkAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        List<AudioChunk>? window = null;
        lock (sync)
        {
            pendingChunks.Add(chunk);
            Snapshot.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Snapshot.ReceivedChunkCount += 1;
            Snapshot.ReceivedAudioBytes += chunk.BytesRecorded;
            Snapshot.LastEncoding = chunk.Encoding;
            Snapshot.LastSampleRate = chunk.SampleRate;
            Snapshot.LastChannels = chunk.Channels;

            var pendingMs = pendingChunks.Sum(WavePcm16Writer.EstimateChunkMilliseconds);
            if (pendingMs >= options.MinimumWindowMilliseconds)
            {
                window = [.. pendingChunks];
                pendingChunks.Clear();
            }
        }

        if (window is null)
        {
            return null;
        }

        var waveBytes = WavePcm16Writer.WriteWaveFile(window, options.TargetSampleRate);
        var text = (await transcriber.TranscribeWaveAsync(waveBytes, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        lock (sync)
        {
            if (string.Equals(lastTranscriptSegment, text, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Skipping duplicate transcript segment for {SessionId}", SessionId);
                return null;
            }

            lastTranscriptSegment = text;
        }

        return new ServerEnvelope
        {
            Type = "transcript",
            SessionId = SessionId,
            Message = "Transcript updated.",
            TranscriptText = text,
            IsFinal = false,
            ReceivedChunkCount = Snapshot.ReceivedChunkCount,
            ReceivedAudioBytes = Snapshot.ReceivedAudioBytes,
        };
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
}
