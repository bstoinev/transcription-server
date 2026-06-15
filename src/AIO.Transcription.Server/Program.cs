using System.Net.WebSockets;
using System.Text;
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
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});
builder.Services.AddSingleton(whisperOptions);
builder.Services.AddLogMachina(x => x.WithNLog(ServiceLifetime.Singleton));
builder.Services.AddHostedService<LoggingLifecycleHostedService>();
builder.Services.AddSingleton<IWaveTranscriber, WhisperCppTranscriber>();
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
    minimumWindowMilliseconds = options.MinimumWindowMilliseconds,
    modelType = options.ModelType,
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
    var buffer = new byte[64 * 1024];
    string currentSessionId = string.Empty;
    logger.Info(
        $"Accepted websocket connection. Remote={context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} Local={context.Connection.LocalIpAddress}:{context.Connection.LocalPort}");

    await SendAsync(socket, new ServerEnvelope
    {
        Type = "server-ready",
        Message = "Connected. Send start-session first."
    }, context.RequestAborted);
    logger.Trace("Sent server-ready envelope.");

    while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
    {
        var message = await ReceiveMessageAsync(socket, buffer, context.RequestAborted);
        if (message is null)
        {
            logger.Info($"Client initiated websocket close. ActiveSessionId={currentSessionId}");
            break;
        }

        ClientEnvelope? clientEnvelope;
        try
        {
            clientEnvelope = JsonSerializer.Deserialize<ClientEnvelope>(message, JsonOptions.Default);
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

        currentSessionId = string.IsNullOrWhiteSpace(clientEnvelope.SessionId) ? currentSessionId : clientEnvelope.SessionId;
        logger.Debug(
            $"Received client envelope. Type={clientEnvelope.Type} SessionId={currentSessionId} Sequence={clientEnvelope.Sequence} PayloadChars={message.Length}");

        try
        {
            switch (clientEnvelope.Type)
            {
                case "start-session":
                    if (string.IsNullOrWhiteSpace(clientEnvelope.SessionId))
                    {
                        await SendAsync(socket, new ServerEnvelope { Type = "error", Message = "sessionId is required for start-session." }, context.RequestAborted);
                        continue;
                    }

                    var started = registry.GetOrCreate(clientEnvelope.SessionId);
                    var startedSnapshot = started.CreateSnapshot();
                    logger.Info(
                        $"Session started. SessionId={started.SessionId} Encoding={clientEnvelope.Encoding ?? "<null>"} SampleRate={clientEnvelope.SampleRate?.ToString() ?? "<null>"} Channels={clientEnvelope.Channels?.ToString() ?? "<null>"}");
                    await SendAsync(socket, new ServerEnvelope
                    {
                        Type = "session-started",
                        SessionId = started.SessionId,
                        Message = "Session registered.",
                        ReceivedChunkCount = startedSnapshot.ReceivedChunkCount,
                        ReceivedAudioBytes = startedSnapshot.ReceivedAudioBytes,
                    }, context.RequestAborted);
                    break;

                case "audio-chunk":
                    if (string.IsNullOrWhiteSpace(currentSessionId))
                    {
                        await SendAsync(socket, new ServerEnvelope { Type = "error", Message = "No active session. Send start-session first." }, context.RequestAborted);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(clientEnvelope.AudioBase64))
                    {
                        await SendAsync(socket, new ServerEnvelope { Type = "error", SessionId = currentSessionId, Message = "audioBase64 is required for audio-chunk." }, context.RequestAborted);
                        continue;
                    }

                    byte[] audioBytes;
                    try
                    {
                        audioBytes = Convert.FromBase64String(clientEnvelope.AudioBase64);
                    }
                    catch (FormatException)
                    {
                        await SendAsync(socket, new ServerEnvelope { Type = "error", SessionId = currentSessionId, Message = "audioBase64 is not valid base64." }, context.RequestAborted);
                        continue;
                    }

                    var session = registry.GetOrCreate(currentSessionId);
                    logger.Debug(
                        $"Decoded audio chunk. SessionId={currentSessionId} Sequence={clientEnvelope.Sequence} AudioBytes={audioBytes.Length} Encoding={clientEnvelope.Encoding ?? "f32le"} SampleRate={clientEnvelope.SampleRate ?? 48000} Channels={clientEnvelope.Channels ?? 2}");
                    var update = await session.AddAudioChunkAsync(new AudioChunk(
                        audioBytes,
                        audioBytes.Length,
                        clientEnvelope.SampleRate ?? 48000,
                        clientEnvelope.Channels ?? 2,
                        clientEnvelope.Encoding ?? "f32le",
                        DateTimeOffset.UtcNow), context.RequestAborted);
                    var sessionSnapshot = session.CreateSnapshot();

                    await SendAsync(socket, new ServerEnvelope
                    {
                        Type = "audio-ack",
                        SessionId = currentSessionId,
                        Message = "Audio chunk received.",
                        ReceivedChunkCount = sessionSnapshot.ReceivedChunkCount,
                        ReceivedAudioBytes = sessionSnapshot.ReceivedAudioBytes,
                    }, context.RequestAborted);
                    logger.Trace(
                        $"Sent audio ack. SessionId={currentSessionId} Sequence={clientEnvelope.Sequence} ReceivedChunkCount={sessionSnapshot.ReceivedChunkCount} ReceivedAudioBytes={sessionSnapshot.ReceivedAudioBytes}");

                    if (update is not null)
                    {
                        logger.Info(
                            $"Transcript update emitted. SessionId={currentSessionId} TranscriptChars={update.TranscriptText?.Length ?? 0} ReceivedChunkCount={update.ReceivedChunkCount ?? 0}");
                        await SendAsync(socket, update, context.RequestAborted);
                    }
                    break;

                case "simulate-text":
                    if (string.IsNullOrWhiteSpace(currentSessionId))
                    {
                        await SendAsync(socket, new ServerEnvelope { Type = "error", Message = "No active session. Send start-session first." }, context.RequestAborted);
                        continue;
                    }

                    registry.GetOrCreate(currentSessionId);
                    logger.Warn($"Simulated transcript requested. SessionId={currentSessionId} IsFinal={clientEnvelope.IsFinalChunk ?? true}");
                    await SendAsync(socket, new ServerEnvelope
                    {
                        Type = "transcript",
                        SessionId = currentSessionId,
                        Message = "Simulated transcript event.",
                        TranscriptText = clientEnvelope.SimulatedText ?? string.Empty,
                        IsFinal = clientEnvelope.IsFinalChunk ?? true,
                    }, context.RequestAborted);
                    break;

                case "end-session":
                    if (string.IsNullOrWhiteSpace(currentSessionId))
                    {
                        await SendAsync(socket, new ServerEnvelope { Type = "error", Message = "No active session to end." }, context.RequestAborted);
                        continue;
                    }

                    var removed = registry.Remove(currentSessionId);
                    var removedSnapshot = removed?.CreateSnapshot();
                    logger.Info(
                        $"Session ended by client. SessionId={currentSessionId} Removed={removed is not null} ReceivedChunkCount={removedSnapshot?.ReceivedChunkCount ?? 0} ReceivedAudioBytes={removedSnapshot?.ReceivedAudioBytes ?? 0}");
                    await SendAsync(socket,
                        removed?.BuildEndedEnvelope() ?? new ServerEnvelope { Type = "session-ended", SessionId = currentSessionId, Message = "Session ended." },
                        context.RequestAborted);
                    currentSessionId = string.Empty;
                    break;

                default:
                    logger.Warn($"Unsupported client envelope type. Type={clientEnvelope.Type} SessionId={currentSessionId}");
                    await SendAsync(socket, new ServerEnvelope
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
                await SendAsync(socket, new ServerEnvelope
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
    }

    logger.Info($"WebSocket handler completed. FinalSocketState={socket.State} FinalSessionId={currentSessionId}");
});

await app.RunAsync();

static async Task<string?> ReceiveMessageAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
{
    using var stream = new MemoryStream();
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

        stream.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
        {
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}

static async Task SendAsync(WebSocket socket, ServerEnvelope envelope, CancellationToken cancellationToken)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions.Default);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
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

static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
