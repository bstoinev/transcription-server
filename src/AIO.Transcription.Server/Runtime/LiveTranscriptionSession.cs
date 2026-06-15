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
    private readonly TranscriptionSessionSnapshot snapshot;
    private string lastTranscriptSegment = string.Empty;

    public LiveTranscriptionSession(string sessionId, IWaveTranscriber transcriber, WhisperTranscriberOptions options, ILogMachina<LiveTranscriptionSession> log)
    {
        SessionId = sessionId;
        this.transcriber = transcriber;
        this.options = options;
        this.log = log;
        snapshot = new TranscriptionSessionSnapshot
        {
            SessionId = sessionId,
            ConnectedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        this.log.Info($"Initialized live transcription session. SessionId={sessionId}");
    }

    public string SessionId { get; }

    public async Task<ServerEnvelope?> AddAudioChunkAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        List<AudioChunk>? window = null;
        TranscriptionSessionSnapshot? snapshotCopy = null;
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

            pendingChunks.Add(chunk);
            snapshot.UpdatedAtUtc = DateTimeOffset.UtcNow;
            snapshot.ReceivedChunkCount += 1;
            snapshot.ReceivedAudioBytes += chunk.BytesRecorded;
            snapshot.LastEncoding = chunk.Encoding;
            snapshot.LastSampleRate = chunk.SampleRate;
            snapshot.LastChannels = chunk.Channels;

            var pendingMs = pendingChunks.Sum(WavePcm16Writer.EstimateChunkMilliseconds);
            log.Trace(
                $"Buffered audio chunk. SessionId={SessionId} ChunkBytes={chunk.BytesRecorded} PendingChunkCount={pendingChunks.Count} PendingMilliseconds={pendingMs:F2} ReceivedChunkCount={snapshot.ReceivedChunkCount}");
            if (pendingMs >= options.MinimumWindowMilliseconds)
            {
                window = [.. pendingChunks];
                pendingChunks.Clear();
                log.Info(
                    $"Transcription window reached. SessionId={SessionId} WindowChunkCount={window.Count} PendingMilliseconds={pendingMs:F2} TargetSampleRate={options.TargetSampleRate}");
            }

            snapshotCopy = CloneSnapshot(snapshot);
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
            ReceivedChunkCount = snapshotCopy?.ReceivedChunkCount,
            ReceivedAudioBytes = snapshotCopy?.ReceivedAudioBytes,
        };
    }

    public ServerEnvelope BuildEndedEnvelope()
    {
        var snapshotCopy = CreateSnapshot();
        return new ServerEnvelope
        {
            Type = "session-ended",
            SessionId = SessionId,
            Message = "Session ended.",
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
