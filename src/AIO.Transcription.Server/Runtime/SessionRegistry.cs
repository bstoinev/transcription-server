using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Transcription;

namespace AIO.Transcription.Server.Runtime;

public sealed class SessionRegistry
{
    private readonly Dictionary<string, LiveTranscriptionSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWaveTranscriber transcriber;
    private readonly WhisperTranscriberOptions options;
    private readonly ILoggerFactory loggerFactory;
    private readonly object sync = new();

    public SessionRegistry(IWaveTranscriber transcriber, WhisperTranscriberOptions options, ILoggerFactory loggerFactory)
    {
        this.transcriber = transcriber;
        this.options = options;
        this.loggerFactory = loggerFactory;
    }

    public LiveTranscriptionSession GetOrCreate(string sessionId)
    {
        lock (sync)
        {
            if (!sessions.TryGetValue(sessionId, out var session))
            {
                session = new LiveTranscriptionSession(sessionId, transcriber, options, loggerFactory.CreateLogger<LiveTranscriptionSession>());
                sessions[sessionId] = session;
            }

            return session;
        }
    }

    public IReadOnlyCollection<TranscriptionSessionSnapshot> GetAll()
    {
        lock (sync)
        {
            return sessions.Values.Select(x => x.Snapshot).OrderBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public LiveTranscriptionSession? Remove(string sessionId)
    {
        lock (sync)
        {
            if (!sessions.TryGetValue(sessionId, out var session))
            {
                return null;
            }

            sessions.Remove(sessionId);
            return session;
        }
    }
}
