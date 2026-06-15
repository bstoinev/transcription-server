using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIO.Transcription.Server;
using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Runtime;
using AIO.Transcription.Server.Transcription;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss ";
    options.SingleLine = true;
});

var whisperOptions = new WhisperTranscriberOptions();
builder.Configuration.GetSection("Transcription").Bind(whisperOptions);

var hostingOptions = new HostingOptions();
builder.Configuration.GetSection(HostingOptions.SectionName).Bind(hostingOptions);

if (!string.IsNullOrWhiteSpace(hostingOptions.Urls))
{
    builder.WebHost.UseUrls(hostingOptions.Urls);
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});
builder.Services.AddSingleton(whisperOptions);
builder.Services.AddSingleton(hostingOptions);
builder.Services.AddSingleton<IWaveTranscriber, WhisperCppTranscriber>();
builder.Services.AddSingleton<SessionRegistry>();

var app = builder.Build();
app.UseWebSockets();

app.MapGet("/", () => Results.Redirect("/healthz"));
app.MapGet("/healthz", (WhisperTranscriberOptions options, HostingOptions hosting) => Results.Ok(new
{
    service = "AIO.Transcription.Server",
    status = "ok",
    timeUtc = DateTimeOffset.UtcNow,
    urls = hosting.Urls,
    minimumWindowMilliseconds = options.MinimumWindowMilliseconds,
    boundaryDetectionEnabled = options.BoundaryDetectionEnabled,
    boundaryDetectionRmsThreshold = options.BoundaryDetectionRmsThreshold,
    boundarySilenceMilliseconds = options.BoundarySilenceMilliseconds,
    minimumSpeechMilliseconds = options.MinimumSpeechMilliseconds,
    maximumSegmentMilliseconds = options.MaximumSegmentMilliseconds,
    modelType = options.ModelType,
    targetSampleRate = options.TargetSampleRate
}));
app.MapGet("/sessions", (SessionRegistry registry) => Results.Ok(registry.GetAll()));

app.Map("/ws/transcribe", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Expected WebSocket request.");
        return;
    }

    var registry = context.RequestServices.GetRequiredService<SessionRegistry>();
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[64 * 1024];
    string currentSessionId = string.Empty;

    await SendAsync(socket, new ServerEnvelope
    {
        Type = "server-ready",
        Message = "Connected. Send start-session first."
    }, context.RequestAborted);

    while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
    {
        var message = await ReceiveMessageAsync(socket, buffer, context.RequestAborted);
        if (message is null)
        {
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

        switch (clientEnvelope.Type)
        {
            case "start-session":
                if (string.IsNullOrWhiteSpace(clientEnvelope.SessionId))
                {
                    await SendAsync(socket, new ServerEnvelope { Type = "error", Message = "sessionId is required for start-session." }, context.RequestAborted);
                    continue;
                }

                var started = registry.GetOrCreate(clientEnvelope.SessionId);
                await SendAsync(socket, new ServerEnvelope
                {
                    Type = "session-started",
                    SessionId = started.SessionId,
                    Message = "Session registered.",
                    ReceivedChunkCount = started.Snapshot.ReceivedChunkCount,
                    ReceivedAudioBytes = started.Snapshot.ReceivedAudioBytes,
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
                var updates = await session.AddAudioChunkAsync(new AudioChunk(
                    audioBytes,
                    audioBytes.Length,
                    clientEnvelope.SampleRate ?? 48000,
                    clientEnvelope.Channels ?? 2,
                    clientEnvelope.Encoding ?? "f32le",
                    DateTimeOffset.UtcNow),
                    clientEnvelope.IsFinalChunk ?? false,
                    context.RequestAborted);

                await SendAsync(socket, new ServerEnvelope
                {
                    Type = "audio-ack",
                    SessionId = currentSessionId,
                    Message = "Audio chunk received.",
                    ReceivedChunkCount = session.Snapshot.ReceivedChunkCount,
                    ReceivedAudioBytes = session.Snapshot.ReceivedAudioBytes,
                }, context.RequestAborted);

                foreach (var update in updates)
                {
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
                if (removed is not null)
                {
                    var finalUpdates = await removed.CompleteAsync(context.RequestAborted);
                    foreach (var finalUpdate in finalUpdates)
                    {
                        await SendAsync(socket, finalUpdate, context.RequestAborted);
                    }
                }

                await SendAsync(socket,
                    removed?.BuildEndedEnvelope() ?? new ServerEnvelope { Type = "session-ended", SessionId = currentSessionId, Message = "Session ended." },
                    context.RequestAborted);
                currentSessionId = string.Empty;
                break;

            default:
                await SendAsync(socket, new ServerEnvelope
                {
                    Type = "error",
                    SessionId = currentSessionId,
                    Message = $"Unsupported message type: {clientEnvelope.Type}"
                }, context.RequestAborted);
                break;
        }
    }
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

static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
