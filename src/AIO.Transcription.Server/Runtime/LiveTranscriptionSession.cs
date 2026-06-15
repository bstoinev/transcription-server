using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Logging;
using AIO.Transcription.Server.Transcription;
using LogMachina;

namespace AIO.Transcription.Server.Runtime;

public sealed class LiveTranscriptionSession
{
    private readonly object sync = new();
    private readonly List<AudioChunk> pendingChunks = [];
    private readonly IWaveTranscriber transcriber;
    private readonly WhisperTranscriberOptions options;
    private readonly ILogMachina<LiveTranscriptionSession> log;
    private string lastTranscriptSegment = string.Empty;

    public LiveTranscriptionSession(string sessionId, IWaveTranscriber transcriber, WhisperTranscriberOptions options, ILogMachina<LiveTranscriptionSession> log)
    {
        SessionId = sessionId;
        this.transcriber = transcriber;
        this.options = options;
        this.log = log;
        Snapshot = new TranscriptionSessionSnapshot
        {
            SessionId = sessionId,
            ConnectedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        this.log.Info($"Initialized live transcription session. SessionId={sessionId}");
    }

    public string SessionId { get; }

    public TranscriptionSessionSnapshot Snapshot { get; }

    public async Task<ServerEnvelope?> AddAudioChunkAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        List<AudioChunk>? window = null;
        lock (sync)
        {
            if (Snapshot.ReceivedChunkCount > 0 &&
                (!string.Equals(Snapshot.LastEncoding, chunk.Encoding, StringComparison.OrdinalIgnoreCase) ||
                 Snapshot.LastSampleRate != chunk.SampleRate ||
                 Snapshot.LastChannels != chunk.Channels))
            {
                log.Warn(
                    $"Audio format changed within active session. SessionId={SessionId} PreviousEncoding={Snapshot.LastEncoding} PreviousSampleRate={Snapshot.LastSampleRate} PreviousChannels={Snapshot.LastChannels} NewEncoding={chunk.Encoding} NewSampleRate={chunk.SampleRate} NewChannels={chunk.Channels}");
            }

            pendingChunks.Add(chunk);
            Snapshot.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Snapshot.ReceivedChunkCount += 1;
            Snapshot.ReceivedAudioBytes += chunk.BytesRecorded;
            Snapshot.LastEncoding = chunk.Encoding;
            Snapshot.LastSampleRate = chunk.SampleRate;
            Snapshot.LastChannels = chunk.Channels;

            var pendingMs = pendingChunks.Sum(WavePcm16Writer.EstimateChunkMilliseconds);
            log.Trace(
                $"Buffered audio chunk. SessionId={SessionId} ChunkBytes={chunk.BytesRecorded} PendingChunkCount={pendingChunks.Count} PendingMilliseconds={pendingMs:F2} ReceivedChunkCount={Snapshot.ReceivedChunkCount}");
            if (pendingMs >= options.MinimumWindowMilliseconds)
            {
                window = [.. pendingChunks];
                pendingChunks.Clear();
                log.Info(
                    $"Transcription window reached. SessionId={SessionId} WindowChunkCount={window.Count} PendingMilliseconds={pendingMs:F2} TargetSampleRate={options.TargetSampleRate}");
            }
        }

        if (window is null)
        {
            return null;
        }

        var waveBytes = WavePcm16Writer.WriteWaveFile(window, options.TargetSampleRate);
        log.Debug($"Prepared wave payload for transcription. SessionId={SessionId} WaveBytes={waveBytes.Length}");
        var text = (await transcriber.TranscribeWaveAsync(waveBytes, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            log.Debug($"Transcription returned no text. SessionId={SessionId}");
            return null;
        }

        lock (sync)
        {
            if (string.Equals(lastTranscriptSegment, text, StringComparison.OrdinalIgnoreCase))
            {
                log.Debug($"Skipping duplicate transcript segment. SessionId={SessionId}");
                return null;
            }

            lastTranscriptSegment = text;
        }

        log.Info($"Transcript segment updated. SessionId={SessionId} TranscriptChars={text.Length}");

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
