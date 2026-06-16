using AIO.Transcription.Server.Logging;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Transcription;
using LogMachina;

namespace AIO.Transcription.Server.Runtime;

public sealed class SessionRegistry
{
    private readonly Dictionary<string, LiveTranscriptionSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWaveTranscriber transcriber;
    private readonly WhisperTranscriberOptions options;
    private readonly ILogMachinaFactory logFactory;
    private readonly ILogMachina<SessionRegistry> log;
    private readonly object sync = new();

    public SessionRegistry(IWaveTranscriber transcriber, WhisperTranscriberOptions options, ILogMachinaFactory logFactory, ILogMachina<SessionRegistry> log)
    {
        this.transcriber = transcriber;
        this.options = options;
        this.logFactory = logFactory;
        this.log = log;
    }

    public bool TryCreate(string sessionId, out LiveTranscriptionSession session)
    {
        lock (sync)
        {
            if (sessions.TryGetValue(sessionId, out session!))
            {
                log.Warn($"Rejected duplicate live transcription session creation. SessionId={sessionId} ActiveSessionCount={sessions.Count}");
                return false;
            }

            session = new LiveTranscriptionSession(sessionId, transcriber, options, logFactory.Create<LiveTranscriptionSession>());
            sessions[sessionId] = session;
            log.Info($"Created live transcription session. SessionId={sessionId} ActiveSessionCount={sessions.Count}");
            return true;
        }
    }

    public bool TryGet(string sessionId, out LiveTranscriptionSession session)
    {
        lock (sync)
        {
            return sessions.TryGetValue(sessionId, out session!);
        }
    }

    public IReadOnlyCollection<TranscriptionSessionSnapshot> GetAll()
    {
        lock (sync)
        {
            return sessions.Values.Select(x => x.CreateSnapshot()).OrderBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public LiveTranscriptionSession? Remove(string sessionId)
    {
        lock (sync)
        {
            if (!sessions.TryGetValue(sessionId, out var session))
            {
                log.Warn($"Requested removal for missing session. SessionId={sessionId} ActiveSessionCount={sessions.Count}");
                return null;
            }

            sessions.Remove(sessionId);
            log.Info($"Removed live transcription session. SessionId={sessionId} ActiveSessionCount={sessions.Count}");
            return session;
        }
    }
}
