using AIO.Transcription.Server.Logging;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Transcription;
using LogMachina;

namespace AIO.Transcription.Server.Runtime;

public sealed class SessionRegistry
{
    private readonly Dictionary<string, LiveTranscriptionSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogMachinaFactory logFactory;
    private readonly ILogMachina<SessionRegistry> log;
    private readonly object sync = new();

    public SessionRegistry(ILogMachinaFactory logFactory, ILogMachina<SessionRegistry> log)
    {
        this.logFactory = logFactory;
        this.log = log;
    }

    public bool TryCreate(string sessionId, WhisperTranscriberOptions options, out LiveTranscriptionSession session, out string? rejectionReason)
    {
        lock (sync)
        {
            rejectionReason = null;
            if (sessions.TryGetValue(sessionId, out session!))
            {
                rejectionReason = "A session with the same sessionId is already active.";
                log.Warn($"Rejected duplicate live transcription session creation. SessionId={sessionId} ActiveSessionCount={sessions.Count}");
                return false;
            }

            if (sessions.Count > 0)
            {
                rejectionReason = "The server already has an active transcription session.";
                log.Warn($"Rejected live transcription session creation because the server is already at capacity. SessionId={sessionId} ActiveSessionCount={sessions.Count}");
                session = null!;
                return false;
            }

            var transcriber = new WhisperCppTranscriber(options, logFactory.Create<WhisperCppTranscriber>());
            session = new LiveTranscriptionSession(sessionId, transcriber, options, logFactory.Create<LiveTranscriptionSession>());
            sessions[sessionId] = session;
            log.Info($"Created live transcription session. SessionId={sessionId} ModelType={options.ModelType} ActiveSessionCount={sessions.Count}");
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
