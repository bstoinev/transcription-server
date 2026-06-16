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
    private string? provisioningSessionId;

    public SessionRegistry(ILogMachinaFactory logFactory, ILogMachina<SessionRegistry> log)
    {
        this.logFactory = logFactory;
        this.log = log;
    }

    public async Task<SessionCreateResult> TryCreateAsync(string sessionId, WhisperTranscriberOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);

        lock (sync)
        {
            if (sessions.TryGetValue(sessionId, out _))
            {
                var rejectionReason = "A session with the same sessionId is already active.";
                log.Warn($"Rejected duplicate live transcription session creation. SessionId={sessionId} ActiveSessionCount={sessions.Count}");
                return SessionCreateResult.FromRejection(rejectionReason);
            }

            if (string.Equals(provisioningSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                const string rejectionReason = "A session with the same sessionId is already being prepared.";
                log.Warn($"Rejected duplicate provisioning request for live transcription session. SessionId={sessionId} ActiveSessionCount={sessions.Count}");
                return SessionCreateResult.FromRejection(rejectionReason);
            }

            if (sessions.Count > 0 || provisioningSessionId is not null)
            {
                var rejectionReason = "The server already has an active transcription session.";
                log.Warn($"Rejected live transcription session creation because the server is already at capacity. SessionId={sessionId} ActiveSessionCount={sessions.Count} ProvisioningSessionId={provisioningSessionId ?? "<none>"}");
                return SessionCreateResult.FromRejection(rejectionReason);
            }

            provisioningSessionId = sessionId;
        }

        WhisperCppTranscriber? transcriber = null;
        try
        {
            transcriber = new WhisperCppTranscriber(options, logFactory.Create<WhisperCppTranscriber>());
            await transcriber.WarmUpAsync(cancellationToken);

            var session = new LiveTranscriptionSession(sessionId, transcriber, options, logFactory.Create<LiveTranscriptionSession>());

            lock (sync)
            {
                sessions[sessionId] = session;
                provisioningSessionId = null;
                log.Info($"Created live transcription session. SessionId={sessionId} ModelType={options.ModelType} ActiveSessionCount={sessions.Count}");
            }

            transcriber = null;
            return SessionCreateResult.FromSession(session);
        }
        catch
        {
            lock (sync)
            {
                if (string.Equals(provisioningSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    provisioningSessionId = null;
                }
            }

            transcriber?.Dispose();
            throw;
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

public sealed record SessionCreateResult(bool Created, LiveTranscriptionSession? Session, string? RejectionReason)
{
    public static SessionCreateResult FromRejection(string reason) => new(false, null, reason);

    public static SessionCreateResult FromSession(LiveTranscriptionSession session) => new(true, session, null);
}
