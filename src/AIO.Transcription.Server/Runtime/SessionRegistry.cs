using AIO.Transcription.Server.Logging;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Transcription;
using LogMachina;

namespace AIO.Transcription.Server.Runtime;

public sealed class SessionRegistry
{
    private readonly Dictionary<string, LiveTranscriptionSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> provisioningSessionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogMachinaFactory logFactory;
    private readonly ILogMachina<SessionRegistry> log;
    private readonly object sync = new();

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
                var rejectionReason = "A live transcription session with the same sessionId already exists.";
                log.Warn(
                    $"Rejected live transcription session creation because the sessionId is already active. RequestedSessionId={sessionId} ActiveSessionCount={sessions.Count} ProvisioningSessionCount={provisioningSessionIds.Count}");
                return SessionCreateResult.FromRejection(rejectionReason);
            }

            if (!provisioningSessionIds.Add(sessionId))
            {
                const string rejectionReason = "A live transcription session with the same sessionId is already being prepared.";
                log.Warn(
                    $"Rejected live transcription session creation because the sessionId is already provisioning. RequestedSessionId={sessionId} ActiveSessionCount={sessions.Count} ProvisioningSessionCount={provisioningSessionIds.Count}");
                return SessionCreateResult.FromRejection(rejectionReason);
            }
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
                provisioningSessionIds.Remove(sessionId);
                log.Info(
                    $"Created live transcription session using client-provided sessionId. SessionId={sessionId} ModelType={options.ModelType} ActiveSessionCount={sessions.Count} ProvisioningSessionCount={provisioningSessionIds.Count}");
            }

            transcriber = null;
            return SessionCreateResult.FromSession(session);
        }
        catch
        {
            lock (sync)
            {
                provisioningSessionIds.Remove(sessionId);
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
