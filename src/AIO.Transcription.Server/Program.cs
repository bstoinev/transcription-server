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
    bufferWindowMilliseconds = options.BufferWindowMillisecondsResolved,
    modelType = WhisperModelCatalog.GetEffectiveConfiguredModelType(options),
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
    LiveTranscriptionSession? activeSession = null;
    Task? updateForwardingTask = null;
    string currentSessionId = string.Empty;
    string? activeEncoding = null;
    int? activeSampleRate = null;
    int? activeChannels = null;
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
            logger.Info($"Client initiated websocket close. ActiveSessionId={currentSessionId}");
            break;
        }

        if (receivedMessage.Value.MessageType == WebSocketMessageType.Binary)
        {
            try
            {
                if (activeSession is null || string.IsNullOrWhiteSpace(currentSessionId))
                {
                    await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", Message = "No active session. Send start-session first." }, context.RequestAborted);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(activeEncoding) || activeSampleRate is null || activeChannels is null)
                {
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = "error",
                        SessionId = currentSessionId,
                        Message = "Binary audio requires encoding, sampleRate, and channels to be established by start-session or a prior audio-chunk."
                    }, context.RequestAborted);
                    continue;
                }

                var audioBytes = receivedMessage.Value.Payload.ToArray();
                await activeSession.AddAudioChunkAsync(new AudioChunk(
                    audioBytes,
                    audioBytes.Length,
                    activeSampleRate.Value,
                    activeChannels.Value,
                    activeEncoding,
                    DateTimeOffset.UtcNow), context.RequestAborted);
                continue;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                logger.Warn($"Request aborted while processing websocket binary audio. SessionId={currentSessionId}");
                break;
            }
            catch (Exception ex)
            {
                logger.Error($"WebSocket binary message processing failed. SessionId={currentSessionId}", ex);
                if (socket.State == WebSocketState.Open)
                {
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = "error",
                        SessionId = currentSessionId,
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

                    if (activeSession is not null)
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope
                        {
                            Type = "error",
                            SessionId = currentSessionId,
                            Message = "A session is already active on this connection."
                        }, context.RequestAborted);
                        continue;
                    }

                    WhisperTranscriberOptions sessionOptions;
                    try
                    {
                        sessionOptions = WhisperModelCatalog.CreateSessionOptions(
                            context.RequestServices.GetRequiredService<WhisperTranscriberOptions>(),
                            clientEnvelope.ModelType,
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
                    activeSession = started;
                    currentSessionId = started.SessionId;
                    activeEncoding = NormalizeEncoding(clientEnvelope.Encoding);
                    activeSampleRate = clientEnvelope.SampleRate;
                    activeChannels = clientEnvelope.Channels;
                    updateForwardingTask = ForwardSessionUpdatesAsync(started, TrySendEnvelopeAsync, logger, context.RequestAborted);
                    var startedSnapshot = started.CreateSnapshot();
                    logger.Info(
                        $"Session started. SessionId={started.SessionId} ModelType={started.ModelType} Encoding={clientEnvelope.Encoding ?? "<null>"} SampleRate={clientEnvelope.SampleRate?.ToString() ?? "<null>"} Channels={clientEnvelope.Channels?.ToString() ?? "<null>"}");
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = "session-started",
                        SessionId = started.SessionId,
                        Message = "Session registered and warmed up.",
                        ModelType = started.ModelType,
                        ReceivedChunkCount = startedSnapshot.ReceivedChunkCount,
                        ReceivedAudioBytes = startedSnapshot.ReceivedAudioBytes,
                    }, context.RequestAborted);
                    break;

                case "audio-chunk":
                    if (activeSession is null || string.IsNullOrWhiteSpace(currentSessionId))
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", Message = "No active session. Send start-session first." }, context.RequestAborted);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(clientEnvelope.AudioBase64))
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", SessionId = currentSessionId, Message = "audioBase64 is required for audio-chunk." }, context.RequestAborted);
                        continue;
                    }

                    byte[] audioBytes;
                    try
                    {
                        audioBytes = Convert.FromBase64String(clientEnvelope.AudioBase64);
                    }
                    catch (FormatException)
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", SessionId = currentSessionId, Message = "audioBase64 is not valid base64." }, context.RequestAborted);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(clientEnvelope.SessionId) &&
                        !string.Equals(clientEnvelope.SessionId, currentSessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope
                        {
                            Type = "error",
                            SessionId = currentSessionId,
                            Message = "audio-chunk sessionId does not match the active session."
                        }, context.RequestAborted);
                        continue;
                    }

                    var resolvedEncoding = NormalizeEncoding(clientEnvelope.Encoding) ?? activeEncoding ?? "f32le";
                    var resolvedSampleRate = clientEnvelope.SampleRate ?? activeSampleRate ?? 48000;
                    var resolvedChannels = clientEnvelope.Channels ?? activeChannels ?? 2;
                    activeEncoding = resolvedEncoding;
                    activeSampleRate = resolvedSampleRate;
                    activeChannels = resolvedChannels;

                    await activeSession.AddAudioChunkAsync(new AudioChunk(
                        audioBytes,
                        audioBytes.Length,
                        resolvedSampleRate,
                        resolvedChannels,
                        resolvedEncoding,
                        DateTimeOffset.UtcNow), context.RequestAborted);
                    break;

                case "simulate-text":
                    if (activeSession is null || string.IsNullOrWhiteSpace(currentSessionId))
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", Message = "No active session. Send start-session first." }, context.RequestAborted);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(clientEnvelope.SessionId) &&
                        !string.Equals(clientEnvelope.SessionId, currentSessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope
                        {
                            Type = "error",
                            SessionId = currentSessionId,
                            Message = "simulate-text sessionId does not match the active session."
                        }, context.RequestAborted);
                        continue;
                    }

                    logger.Warn($"Simulated transcript requested. SessionId={currentSessionId} IsFinal={clientEnvelope.IsFinalChunk ?? true}");
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = "transcript",
                        SessionId = currentSessionId,
                        Message = "Simulated transcript event.",
                        TranscriptText = clientEnvelope.SimulatedText ?? string.Empty,
                        IsFinal = clientEnvelope.IsFinalChunk ?? true,
                    }, context.RequestAborted);
                    break;

                case "end-session":
                    if (activeSession is null || string.IsNullOrWhiteSpace(currentSessionId))
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope { Type = "error", Message = "No active session to end." }, context.RequestAborted);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(clientEnvelope.SessionId) &&
                        !string.Equals(clientEnvelope.SessionId, currentSessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        await TrySendEnvelopeAsync(new ServerEnvelope
                        {
                            Type = "error",
                            SessionId = currentSessionId,
                            Message = "end-session sessionId does not match the active session."
                        }, context.RequestAborted);
                        continue;
                    }

                    await activeSession.CompleteAsync(flushPendingAudio: true, context.RequestAborted);
                    if (updateForwardingTask is not null)
                    {
                        await updateForwardingTask;
                    }

                    var removed = registry.Remove(currentSessionId);
                    if (removed is not null)
                    {
                        await removed.DisposeAsync();
                    }

                    var removedSnapshot = removed?.CreateSnapshot();
                    logger.Info(
                        $"Session ended by client. SessionId={currentSessionId} Removed={removed is not null} ReceivedChunkCount={removedSnapshot?.ReceivedChunkCount ?? 0} ReceivedAudioBytes={removedSnapshot?.ReceivedAudioBytes ?? 0}");
                    await TrySendEnvelopeAsync(
                        removed?.BuildEndedEnvelope() ?? new ServerEnvelope { Type = "session-ended", SessionId = currentSessionId, Message = "Session ended." },
                        context.RequestAborted);
                    activeSession = null;
                    updateForwardingTask = null;
                    activeEncoding = null;
                    activeSampleRate = null;
                    activeChannels = null;
                    currentSessionId = string.Empty;
                    break;

                default:
                    logger.Warn($"Unsupported client envelope type. Type={clientEnvelope.Type} SessionId={currentSessionId}");
                    await TrySendEnvelopeAsync(new ServerEnvelope
                    {
                        Type = "error",
                        SessionId = currentSessionId,
                        Message = $"Unsupported message type: {clientEnvelope.Type}"
                    }, context.RequestAborted);
                    break;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.Warn($"Request aborted while processing websocket message. SessionId={currentSessionId}");
            break;
        }
        catch (Exception ex)
        {
            logger.Error(
                $"WebSocket message processing failed. Type={clientEnvelope.Type} SessionId={currentSessionId} Sequence={clientEnvelope.Sequence}",
                ex);

            if (socket.State == WebSocketState.Open)
            {
                await TrySendEnvelopeAsync(new ServerEnvelope
                {
                    Type = "error",
                    SessionId = currentSessionId,
                    Message = $"Server failed to process {clientEnvelope.Type}: {ex.Message}"
                }, context.RequestAborted);

                await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "server-processing-failed", context.RequestAborted);
            }

            break;
        }
    }

    if (!string.IsNullOrWhiteSpace(currentSessionId))
    {
        var removed = registry.Remove(currentSessionId);
        var removedSnapshot = removed?.CreateSnapshot();
        logger.Warn(
            $"WebSocket handler exited with an active session. SessionId={currentSessionId} Removed={removed is not null} ReceivedChunkCount={removedSnapshot?.ReceivedChunkCount ?? 0} ReceivedAudioBytes={removedSnapshot?.ReceivedAudioBytes ?? 0}");
        if (removed is not null)
        {
            await removed.DisposeAsync();
        }
    }

    if (updateForwardingTask is not null)
    {
        try
        {
            await updateForwardingTask;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }

    logger.Info($"WebSocket handler completed. FinalSocketState={socket.State} FinalSessionId={currentSessionId}");

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
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken);
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
        if (string.Equals(update.Type, "transcript", StringComparison.OrdinalIgnoreCase))
        {
            logger.Info(
                $"Transcript update emitted. SessionId={update.SessionId} TranscriptChars={update.TranscriptText?.Length ?? 0} ReceivedChunkCount={update.ReceivedChunkCount ?? 0} IsFinal={update.IsFinal ?? false}");
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

readonly record struct ReceivedSocketMessage(WebSocketMessageType MessageType, ReadOnlyMemory<byte> Payload);
