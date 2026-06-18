using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Logging;
using AIO.Transcription.Server.Runtime;
using AIO.Transcription.Server.Transcription;
using LogMachina;
using LogMachina.DependencyInjection;
using NLog;
using NLog.Config;
using NLog.Targets;

var builder = WebApplication.CreateBuilder(args);
WriteBootstrapTrace(builder.Environment.ContentRootPath, "Process entered Main.");
ConfigureNLog(builder.Environment);
WriteBootstrapTrace(builder.Environment.ContentRootPath, "NLog configuration completed.");

var whisperOptions = new WhisperTranscriberOptions();
builder.Configuration.GetSection("Transcription").Bind(whisperOptions);
whisperOptions.Validate();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonOptions.Http.PropertyNamingPolicy;
    options.SerializerOptions.WriteIndented = JsonOptions.Http.WriteIndented;
});
builder.Services.AddSingleton(whisperOptions);
builder.Services.AddLogMachina(x => x.WithNLog(ServiceLifetime.Singleton));
builder.Services.AddHostedService<LoggingLifecycleHostedService>();
builder.Services.AddSingleton<SessionRegistry>();

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(LogManager.Shutdown);
app.UseWebSockets();

app.MapGet("/", () => Results.Redirect("/healthz"));
app.MapGet("/healthz", (WhisperTranscriberOptions options) => Results.Ok(new
{
    service = "AIO.Transcription.Server",
    status = "ok",
    timeUtc = DateTimeOffset.UtcNow,
    partialUpdateIntervalMs = options.PartialUpdateIntervalMs,
    partialWindowMs = options.PartialWindowMs,
    minimumUtteranceMs = options.MinimumUtteranceMs,
    endSilenceMs = options.EndSilenceMs,
    maxUtteranceMs = options.MaxUtteranceMs,
    modelType = WhisperModelCatalog.SupportsSessionModelSelection(options)
        ? null
        : WhisperModelCatalog.GetEffectiveConfiguredModelType(options),
    language = options.Language,
    targetSampleRate = options.TargetSampleRate
}));
app.MapGet("/sessions", (SessionRegistry registry) => Results.Ok(registry.GetAll()));

app.Map("/ws/transcribe", async context =>
{
    var logger = context.RequestServices.GetRequiredService<ILogMachinaFactory>().Create<TranscriptionWebSocketEndpoint>();
    if (!context.WebSockets.IsWebSocketRequest)
    {
        logger.Warn($"Rejected non-websocket request. Method={context.Request.Method} Path={context.Request.Path}");
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Expected WebSocket request.");
        return;
    }

    var registry = context.RequestServices.GetRequiredService<SessionRegistry>();
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var sendLock = new SemaphoreSlim(1, 1);
    var buffer = new byte[64 * 1024];
    var connectionSessions = new Dictionary<string, ConnectionSessionState>(StringComparer.OrdinalIgnoreCase);
    logger.Info(
        $"Accepted websocket connection. Remote={context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} Local={context.Connection.LocalIpAddress}:{context.Connection.LocalPort}");

    var capabilities = WhisperModelCatalog.BuildCapabilities(
        context.RequestServices.GetRequiredService<WhisperTranscriberOptions>());
    await TrySendEnvelopeAsync(new ServerEnvelope
    {
        Type = "server-ready",
        Message = "Connected. Select a model and send start-session first.",
        Capabilities = capabilities
    }, context.RequestAborted);
    while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
    {
        var receivedMessage = await ReceiveMessageAsync(socket, buffer, context.RequestAborted);
        if (receivedMessage is null)
        {
            logger.Info($"Client initiated websocket close. ActiveSessionCount={connectionSessions.Count} ActiveSessionIds={FormatSessionIds(connectionSessions.Keys)}");
            break;
        }

        if (receivedMessage.Value.MessageType == WebSocketMessageType.Binary)
        {
            ConnectionSessionState? binarySession = null;
            try
            {
                if (connectionSessions.Count == 0)
                {
                    await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", Message = "No active session. Send start-session first." }, context.RequestAborted);
                    continue;
                }

                if (connectionSessions.Count > 1)
                {
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = "error",
                        Message = "Binary audio requires exactly one active session on the websocket. Use JSON audio-chunk with sessionId when multiple sessions are active."
                    }, context.RequestAborted);
                    continue;
                }

                binarySession = connectionSessions.Values.First();
                if (string.IsNullOrWhiteSpace(binarySession.ActiveEncoding) || binarySession.ActiveSampleRate is null || binarySession.ActiveChannels is null)
                {
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = "error",
                        SessionId = binarySession.SessionId,
                        Message = "Binary audio requires encoding, sampleRate, and channels to be established by start-session or a prior audio-chunk."
                    }, context.RequestAborted);
                    continue;
                }

                var audioBytes = receivedMessage.Value.Payload.ToArray();
                await binarySession.Session.AddAudioChunkAsync(new AudioChunk(
                    audioBytes,
                    audioBytes.Length,
                    binarySession.ActiveSampleRate.Value,
                    binarySession.ActiveChannels.Value,
                    binarySession.ActiveEncoding,
                    DateTimeOffset.UtcNow), context.RequestAborted);
                continue;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                logger.Warn($"Request aborted while processing websocket binary audio. SessionId={binarySession?.SessionId ?? "<none>"}");
                break;
            }
            catch (Exception ex)
            {
                logger.Error($"WebSocket binary message processing failed. SessionId={binarySession?.SessionId ?? "<none>"}", ex);
                if (socket.State == WebSocketState.Open)
                {
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = "error",
                        SessionId = binarySession?.SessionId ?? string.Empty,
                        Message = $"Server failed to process binary audio: {ex.Message}"
                    }, context.RequestAborted);
                    await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "server-processing-failed", context.RequestAborted);
                }

                break;
            }
        }

        ClientEnvelope? clientEnvelope;
        try
        {
            clientEnvelope = JsonSerializer.Deserialize<ClientEnvelope>(receivedMessage.Value.Payload.Span, JsonOptions.WebSocket);
        }
        catch (JsonException ex)
        {
            await SendAsync(socket, new ServerEnvelope
            {
                Type = "error",
                Message = $"Invalid JSON envelope: {ex.Message}"
            }, context.RequestAborted);
            continue;
        }

        if (clientEnvelope is null)
        {
            await SendAsync(socket, new ServerEnvelope
            {
                Type = "error",
                Message = "Empty JSON envelope."
            }, context.RequestAborted);
            continue;
        }

        try
        {
            switch (clientEnvelope.Type)
            {
                case "start-session":
                    if (string.IsNullOrWhiteSpace(clientEnvelope.SessionId))
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", Message = "sessionId is required for start-session." }, context.RequestAborted);
                        continue;
                    }

                    if (connectionSessions.TryGetValue(clientEnvelope.SessionId, out var existingConnectionSession))
                    {
                        logger.Warn(
                            $"Rejected start-session because the sessionId is already active on this websocket. RequestedSessionId={clientEnvelope.SessionId} ActiveSessionId={existingConnectionSession.SessionId}");
                        await TrySendEnvelopeAsync(new ServerEnvelope
                        {
                            Type = "error",
                            SessionId = existingConnectionSession.SessionId,
                            Message = "A session with this sessionId is already active on this connection."
                        }, context.RequestAborted);
                        continue;
                    }

                    WhisperTranscriberOptions sessionOptions;
                    try
                    {
                        sessionOptions = WhisperModelCatalog.CreateSessionOptions(
                            context.RequestServices.GetRequiredService<WhisperTranscriberOptions>(),
                            clientEnvelope.ModelType,
                            clientEnvelope.Prompt,
                            clientEnvelope.Language,
                            clientEnvelope.EnableLanguageDetection);
                    }
                    catch (InvalidOperationException ex)
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope
                        {
                            Type = "error",
                            SessionId = clientEnvelope.SessionId,
                            Message = ex.Message
                        }, context.RequestAborted);
                        continue;
                    }

                    SessionCreateResult createResult;
                    try
                    {
                        createResult = await registry.TryCreateAsync(clientEnvelope.SessionId, sessionOptions, context.RequestAborted);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(
                            $"Session warm-up failed. SessionId={clientEnvelope.SessionId} ModelType={sessionOptions.ModelType}",
                            ex);
                        await TrySendEnvelopeAsync(new ServerEnvelope
                        {
                            Type = "error",
                            SessionId = clientEnvelope.SessionId,
                            ModelType = sessionOptions.ModelType,
                            Message = $"Unable to start session: {ex.Message}"
                        }, context.RequestAborted);
                        continue;
                    }

                    if (!createResult.Created || createResult.Session is null)
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope
                        {
                            Type = "error",
                            SessionId = clientEnvelope.SessionId,
                            ModelType = sessionOptions.ModelType,
                            Message = createResult.RejectionReason ?? "Unable to create session."
                        }, context.RequestAborted);
                        continue;
                    }

                    var started = createResult.Session;
                    var startedState = new ConnectionSessionState(
                        started,
                        ForwardSessionUpdatesAsync(started, TrySendEnvelopeAsync, logger, context.RequestAborted))
                    {
                        ActiveEncoding = NormalizeEncoding(clientEnvelope.Encoding),
                        ActiveSampleRate = clientEnvelope.SampleRate,
                        ActiveChannels = clientEnvelope.Channels
                    };
                    connectionSessions[started.SessionId] = startedState;
                    var startedSnapshot = started.CreateSnapshot();
                    logger.Info(
                        $"Session started using client-provided sessionId. SessionId={started.SessionId} ModelType={started.ModelType} Encoding={clientEnvelope.Encoding ?? "<null>"} SampleRate={clientEnvelope.SampleRate?.ToString() ?? "<null>"} Channels={clientEnvelope.Channels?.ToString() ?? "<null>"} ConnectionSessionCount={connectionSessions.Count}");
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = "session-started",
                        SessionId = started.SessionId,
                        Message = "Live transcription session started for the provided sessionId.",
                        ModelType = started.ModelType,
                        ReceivedChunkCount = startedSnapshot.ReceivedChunkCount,
                        ReceivedAudioBytes = startedSnapshot.ReceivedAudioBytes,
                    }, context.RequestAborted);
                    break;

                case "audio-chunk":
                    var audioSession = await ResolveConnectionSessionAsync(clientEnvelope.SessionId, "audio-chunk", "No active session. Send start-session first.");
                    if (audioSession is null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(clientEnvelope.AudioBase64))
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", SessionId = audioSession.SessionId, Message = "audioBase64 is required for audio-chunk." }, context.RequestAborted);
                        continue;
                    }

                    byte[] audioBytes;
                    try
                    {
                        audioBytes = Convert.FromBase64String(clientEnvelope.AudioBase64);
                    }
                    catch (FormatException)
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", SessionId = audioSession.SessionId, Message = "audioBase64 is not valid base64." }, context.RequestAborted);
                        continue;
                    }

                    var resolvedEncoding = NormalizeEncoding(clientEnvelope.Encoding) ?? audioSession.ActiveEncoding ?? "f32le";
                    var resolvedSampleRate = clientEnvelope.SampleRate ?? audioSession.ActiveSampleRate ?? 48000;
                    var resolvedChannels = clientEnvelope.Channels ?? audioSession.ActiveChannels ?? 2;
                    audioSession.ActiveEncoding = resolvedEncoding;
                    audioSession.ActiveSampleRate = resolvedSampleRate;
                    audioSession.ActiveChannels = resolvedChannels;

                    await audioSession.Session.AddAudioChunkAsync(new AudioChunk(
                        audioBytes,
                        audioBytes.Length,
                        resolvedSampleRate,
                        resolvedChannels,
                        resolvedEncoding,
                        DateTimeOffset.UtcNow), context.RequestAborted);
                    break;

                case "simulate-text":
                    var simulatedSession = await ResolveConnectionSessionAsync(clientEnvelope.SessionId, "simulate-text", "No active session. Send start-session first.");
                    if (simulatedSession is null)
                    {
                        continue;
                    }

                    var simulatedFinalEvent = clientEnvelope.IsFinalChunk ?? true;
                    logger.Warn($"Simulated transcript requested. SessionId={simulatedSession.SessionId} FinalEvent={simulatedFinalEvent}");
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = simulatedFinalEvent ? "final-transcript" : "partial-transcript",
                        SessionId = simulatedSession.SessionId,
                        Message = "Simulated transcript event.",
                        UtteranceId = $"{simulatedSession.SessionId}-simulated",
                        Sequence = simulatedFinalEvent ? null : 1,
                        TranscriptText = clientEnvelope.SimulatedText ?? string.Empty,
                    }, context.RequestAborted);
                    break;

                case "end-session":
                    logger.Info(
                        $"Received end-session request. RequestedSessionId={clientEnvelope.SessionId ?? "<null>"} ActiveSessionCount={connectionSessions.Count}");
                    var endingSession = await ResolveConnectionSessionAsync(clientEnvelope.SessionId, "end-session", "No active session to end.");
                    if (endingSession is null)
                    {
                        continue;
                    }

                    var shutdownCompleted = await ShutdownConnectionSessionAsync(
                        endingSession.SessionId,
                        shutdownReason: "client-end-session",
                        flushPendingAudio: true,
                        sendSessionEnded: true,
                        context.RequestAborted);
                    if (!shutdownCompleted)
                    {
                        break;
                    }
                    break;

                default:
                    logger.Warn($"Unsupported client envelope type. Type={clientEnvelope.Type} SessionId={clientEnvelope.SessionId}");
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = "error",
                        SessionId = clientEnvelope.SessionId,
                        Message = $"Unsupported message type: {clientEnvelope.Type}"
                    }, context.RequestAborted);
                    break;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.Warn($"Request aborted while processing websocket message. SessionId={clientEnvelope.SessionId}");
            break;
        }
        catch (Exception ex)
        {
            logger.Error(
                $"WebSocket message processing failed. Type={clientEnvelope.Type} SessionId={clientEnvelope.SessionId} Sequence={clientEnvelope.Sequence}",
                ex);

            if (socket.State == WebSocketState.Open)
            {
                await TrySendEnvelopeAsync(new ServerEnvelope
                {
                    Type = "error",
                    SessionId = clientEnvelope.SessionId,
                    Message = $"Server failed to process {clientEnvelope.Type}: {ex.Message}"
                }, context.RequestAborted);

                await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "server-processing-failed", context.RequestAborted);
            }

            break;
        }
    }

    if (connectionSessions.Count > 0)
    {
        var remainingSessionIds = connectionSessions.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        logger.Warn($"WebSocket handler exited with active sessions. ActiveSessionCount={remainingSessionIds.Length} ActiveSessionIds={FormatSessionIds(remainingSessionIds)}");
        foreach (var sessionId in remainingSessionIds)
        {
            await ShutdownConnectionSessionAsync(
                sessionId,
                shutdownReason: "websocket-handler-exit",
                flushPendingAudio: false,
                sendSessionEnded: false,
                CancellationToken.None);
        }
    }

    logger.Info($"WebSocket handler completed. FinalSocketState={socket.State} FinalSessionCount={connectionSessions.Count}");

    async Task<bool> TrySendEnvelopeAsync(ServerEnvelope envelope, CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return false;
            }

            await SendAsync(socket, envelope, cancellationToken);
            return true;
        }
        finally
        {
            sendLock.Release();
        }
    }

    async Task<ConnectionSessionState?> ResolveConnectionSessionAsync(
        string? requestedSessionId,
        string messageType,
        string noActiveSessionMessage)
    {
        if (connectionSessions.Count == 0)
        {
            await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", Message = noActiveSessionMessage }, context.RequestAborted);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requestedSessionId))
        {
            if (connectionSessions.TryGetValue(requestedSessionId, out var requestedSession))
            {
                return requestedSession;
            }

            await TrySendEnvelopeAsync(new ServerEnvelope
            {
                Type = "error",
                SessionId = requestedSessionId,
                Message = $"{messageType} sessionId does not match an active session on this connection."
            }, context.RequestAborted);
            return null;
        }

        if (connectionSessions.Count == 1)
        {
            return connectionSessions.Values.First();
        }

        await TrySendEnvelopeAsync(new ServerEnvelope
        {
            Type = "error",
            Message = $"{messageType} requires sessionId when multiple sessions are active on this connection."
        }, context.RequestAborted);
        return null;
    }

    async Task<bool> ShutdownConnectionSessionAsync(
        string sessionId,
        string shutdownReason,
        bool flushPendingAudio,
        bool sendSessionEnded,
        CancellationToken cancellationToken)
    {
        if (!connectionSessions.TryGetValue(sessionId, out var sessionState))
        {
            logger.Warn($"Shutdown requested without an active session. SessionId={sessionId} Reason={shutdownReason}");
            return true;
        }

        var session = sessionState.Session;
        var forwardingTask = sessionState.UpdateForwardingTask;
        Exception? shutdownFailure = null;
        LiveTranscriptionSession? sessionToDispose = null;
        ServerEnvelope endedEnvelope = session.BuildEndedEnvelope();

        logger.Info(
            $"Beginning session shutdown. SessionId={sessionId} Reason={shutdownReason} FlushPendingAudio={flushPendingAudio} HasUpdateForwarder={forwardingTask is not null} ConnectionSessionCount={connectionSessions.Count}");

        try
        {
            await session.CompleteAsync(flushPendingAudio, cancellationToken);
            if (forwardingTask is not null)
            {
                await forwardingTask;
            }

            endedEnvelope = session.BuildEndedEnvelope();
        }
        catch (Exception ex)
        {
            shutdownFailure = ex;
            logger.Error($"Session shutdown failed before cleanup. SessionId={sessionId} Reason={shutdownReason}", ex);
        }
        finally
        {
            var removed = registry.Remove(sessionId);
            sessionToDispose = removed ?? session;
            var removedFromConnection = connectionSessions.Remove(sessionId);
            var removedSnapshot = sessionToDispose.CreateSnapshot();
            logger.Info(
                $"Clearing active session from connection. SessionId={sessionId} Reason={shutdownReason} RemovedFromRegistry={removed is not null} RemovedFromConnection={removedFromConnection} RemainingConnectionSessionCount={connectionSessions.Count} ReceivedChunkCount={removedSnapshot.ReceivedChunkCount} ReceivedAudioBytes={removedSnapshot.ReceivedAudioBytes}");

            try
            {
                await sessionToDispose.DisposeAsync();
            }
            catch (Exception ex)
            {
                if (shutdownFailure is null)
                {
                    shutdownFailure = ex;
                }

                logger.Error($"Session disposal failed during shutdown. SessionId={sessionId} Reason={shutdownReason}", ex);
            }
        }

        if (shutdownFailure is null && sendSessionEnded)
        {
            logger.Info($"Sending session-ended envelope. SessionId={sessionId} Reason={shutdownReason}");
            var sent = await TrySendEnvelopeAsync(endedEnvelope, cancellationToken);
            logger.Info($"Completed session shutdown. SessionId={sessionId} Reason={shutdownReason} SessionEndedSent={sent}");
            return sent;
        }

        if (shutdownFailure is null)
        {
            logger.Info($"Completed session shutdown. SessionId={sessionId} Reason={shutdownReason} SessionEndedSent={sendSessionEnded}");
            return true;
        }

        logger.Error(
            $"Session shutdown failed after cleanup. SessionId={sessionId} Reason={shutdownReason}. Closing websocket to avoid stale connection reuse.",
            shutdownFailure);

        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "session-shutdown-failed", CancellationToken.None);
            }
            catch (Exception closeEx)
            {
                logger.Error($"Failed to close websocket after shutdown failure. SessionId={sessionId} Reason={shutdownReason}", closeEx);
            }
        }

        return false;
    }

    static string FormatSessionIds(IEnumerable<string> sessionIds)
    {
        var materialized = sessionIds.Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return materialized.Length == 0 ? "<none>" : string.Join(",", materialized);
    }
});

await app.RunAsync();

static async Task<ReceivedSocketMessage?> ReceiveMessageAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
{
    var writer = new ArrayBufferWriter<byte>();
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    result.CloseStatusDescription ?? "closing",
                    cancellationToken);
            }

            return null;
        }

        var destination = writer.GetSpan(result.Count);
        buffer.AsSpan(0, result.Count).CopyTo(destination);
        writer.Advance(result.Count);
        if (result.EndOfMessage)
        {
            return new ReceivedSocketMessage(result.MessageType, writer.WrittenMemory);
        }
    }
}

static async Task SendAsync(WebSocket socket, ServerEnvelope envelope, CancellationToken cancellationToken)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions.WebSocket);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
}

static async Task ForwardSessionUpdatesAsync(
    LiveTranscriptionSession session,
    Func<ServerEnvelope, CancellationToken, Task<bool>> trySendEnvelopeAsync,
    ILogMachina<TranscriptionWebSocketEndpoint> logger,
    CancellationToken cancellationToken)
{
    await foreach (var update in session.ReadUpdatesAsync(cancellationToken))
    {
        if (string.Equals(update.Type, "partial-transcript", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(update.Type, "final-transcript", StringComparison.OrdinalIgnoreCase))
        {
            logger.Info(
                $"Transcript update emitted. Type={update.Type} SessionId={update.SessionId} UtteranceId={update.UtteranceId ?? "<none>"} Sequence={update.Sequence?.ToString() ?? "<none>"} TranscriptChars={update.TranscriptText?.Length ?? 0} ReceivedChunkCount={update.ReceivedChunkCount ?? 0}");
        }
        else if (string.Equals(update.Type, "error", StringComparison.OrdinalIgnoreCase))
        {
            logger.Error($"Session update emitted error. SessionId={update.SessionId} Message={update.Message}");
        }
        else
        {
            logger.Info($"Session update emitted. Type={update.Type} SessionId={update.SessionId}");
        }

        if (!await trySendEnvelopeAsync(update, cancellationToken))
        {
            logger.Warn($"Unable to send session update because the websocket is no longer open. SessionId={update.SessionId}");
            break;
        }
    }
}

static void ConfigureNLog(IHostEnvironment environment)
{
    try
    {
        GlobalDiagnosticsContext.Set("LogRoot", environment.ContentRootPath);
        var contentRootConfigPath = Path.Combine(environment.ContentRootPath, "NLog.config");
        var baseDirectoryConfigPath = Path.Combine(AppContext.BaseDirectory, "NLog.config");
        WriteBootstrapTrace(
            environment.ContentRootPath,
            $"ConfigureNLog: ContentRoot={environment.ContentRootPath} BaseDirectory={AppContext.BaseDirectory} ContentRootConfigExists={File.Exists(contentRootConfigPath)} BaseDirectoryConfigExists={File.Exists(baseDirectoryConfigPath)}");
        if (File.Exists(contentRootConfigPath))
        {
            LogManager.Setup().LoadConfigurationFromFile(contentRootConfigPath);
            return;
        }

        if (File.Exists(baseDirectoryConfigPath))
        {
            LogManager.Setup().LoadConfigurationFromFile(baseDirectoryConfigPath);
            return;
        }

        LogManager.Configuration = BuildFallbackLoggingConfiguration(environment.ContentRootPath);
    }
    catch (Exception ex)
    {
        WriteBootstrapTrace(environment.ContentRootPath, $"ConfigureNLog failed: {ex}");
        throw;
    }
}

static LoggingConfiguration BuildFallbackLoggingConfiguration(string logRoot)
{
    var layout = "${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner=|${exception:format=ToString}}";
    var config = new LoggingConfiguration();

    var consoleTarget = new ConsoleTarget("console")
    {
        Layout = layout
    };

    var fileTarget = new FileTarget("file")
    {
        FileName = Path.Combine(logRoot, "transcription-server.log"),
        KeepFileOpen = false,
        CreateDirs = true,
        Layout = layout
    };

    var errorTarget = new FileTarget("errors")
    {
        FileName = Path.Combine(logRoot, "transcription-server.errors.log"),
        KeepFileOpen = false,
        CreateDirs = true,
        Layout = layout
    };

    config.AddTarget(consoleTarget);
    config.AddTarget(fileTarget);
    config.AddTarget(errorTarget);

    config.AddRuleForAllLevels(fileTarget);
    config.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, consoleTarget);
    config.AddRule(NLog.LogLevel.Error, NLog.LogLevel.Fatal, errorTarget);
    return config;
}

static void WriteBootstrapTrace(string contentRootPath, string message)
{
    try
    {
        var lines = $"{DateTimeOffset.UtcNow:O}|PID={Environment.ProcessId}|{message}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(contentRootPath, "bootstrap.log"), lines);
    }
    catch
    {
    }

    try
    {
        var lines = $"{DateTimeOffset.UtcNow:O}|PID={Environment.ProcessId}|{message}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "bootstrap.log"), lines);
    }
    catch
    {
    }
}

static string? NormalizeEncoding(string? encoding)
{
    if (string.IsNullOrWhiteSpace(encoding))
    {
        return null;
    }

    return encoding.Trim();
}

static class JsonOptions
{
    public static readonly JsonSerializerOptions WebSocket = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Http = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

sealed class ConnectionSessionState
{
    public ConnectionSessionState(LiveTranscriptionSession session, Task updateForwardingTask)
    {
        Session = session;
        UpdateForwardingTask = updateForwardingTask;
    }

    public LiveTranscriptionSession Session { get; }
    public Task UpdateForwardingTask { get; }
    public string SessionId => Session.SessionId;
    public string? ActiveEncoding { get; set; }
    public int? ActiveSampleRate { get; set; }
    public int? ActiveChannels { get; set; }
}

readonly record struct ReceivedSocketMessage(WebSocketMessageType MessageType, ReadOnlyMemory<byte> Payload);
