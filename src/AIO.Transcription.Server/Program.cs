using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIO.Transcription.Server.Contracts.Protocol;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});

builder.Services.AddSingleton<TranscriptionSessionStore>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => Results.Redirect("/healthz"));
app.MapGet("/healthz", () => Results.Ok(new
{
    service = "AIO.Transcription.Server",
    status = "ok",
    timeUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/sessions", (TranscriptionSessionStore store) => Results.Ok(store.GetAll()));

app.Map("/ws/transcribe", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Expected WebSocket request.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var store = context.RequestServices.GetRequiredService<TranscriptionSessionStore>();
    var buffer = new byte[1024 * 64];
    string currentSessionId = string.Empty;

    await SendAsync(socket, new ServerEnvelope
    {
        Type = "server-ready",
        Message = "Connected. Send start-session first."
    });

    while (socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", context.RequestAborted);
            break;
        }

        var message = await ReadMessageAsync(socket, buffer, result, context.RequestAborted);
        var clientEnvelope = JsonSerializer.Deserialize<ClientEnvelope>(message, JsonOptions.Default);
        if (clientEnvelope is null)
        {
            await SendAsync(socket, new ServerEnvelope
            {
                Type = "error",
                Message = "Invalid JSON envelope."
            });
            continue;
        }

        currentSessionId = string.IsNullOrWhiteSpace(clientEnvelope.SessionId) ? currentSessionId : clientEnvelope.SessionId;

        switch (clientEnvelope.Type)
        {
            case "start-session":
                if (string.IsNullOrWhiteSpace(clientEnvelope.SessionId))
                {
                    await SendAsync(socket, new ServerEnvelope
                    {
                        Type = "error",
                        Message = "sessionId is required for start-session."
                    });
                    continue;
                }

                var snapshot = store.StartOrUpdate(clientEnvelope.SessionId, clientEnvelope.Encoding, clientEnvelope.SampleRate, clientEnvelope.Channels, 0);
                await SendAsync(socket, new ServerEnvelope
                {
                    Type = "session-started",
                    SessionId = snapshot.SessionId,
                    Message = "Session registered.",
                    ReceivedChunkCount = snapshot.ReceivedChunkCount,
                    ReceivedAudioBytes = snapshot.ReceivedAudioBytes
                });
                break;

            case "audio-chunk":
                if (string.IsNullOrWhiteSpace(currentSessionId))
                {
                    await SendAsync(socket, new ServerEnvelope
                    {
                        Type = "error",
                        Message = "No active session. Send start-session first."
                    });
                    continue;
                }

                var bytes = 0L;
                if (!string.IsNullOrWhiteSpace(clientEnvelope.AudioBase64))
                {
                    try
                    {
                        bytes = Convert.FromBase64String(clientEnvelope.AudioBase64).LongLength;
                    }
                    catch (FormatException)
                    {
                        await SendAsync(socket, new ServerEnvelope
                        {
                            Type = "error",
                            SessionId = currentSessionId,
                            Message = "audioBase64 is not valid base64."
                        });
                        continue;
                    }
                }

                var updated = store.StartOrUpdate(currentSessionId, clientEnvelope.Encoding, clientEnvelope.SampleRate, clientEnvelope.Channels, bytes);
                await SendAsync(socket, new ServerEnvelope
                {
                    Type = "audio-ack",
                    SessionId = updated.SessionId,
                    Message = "Audio chunk received.",
                    ReceivedChunkCount = updated.ReceivedChunkCount,
                    ReceivedAudioBytes = updated.ReceivedAudioBytes
                });
                break;

            case "simulate-text":
                if (string.IsNullOrWhiteSpace(currentSessionId))
                {
                    await SendAsync(socket, new ServerEnvelope
                    {
                        Type = "error",
                        Message = "No active session. Send start-session first."
                    });
                    continue;
                }

                store.StartOrUpdate(currentSessionId, clientEnvelope.Encoding, clientEnvelope.SampleRate, clientEnvelope.Channels, 0);
                await SendAsync(socket, new ServerEnvelope
                {
                    Type = "transcript",
                    SessionId = currentSessionId,
                    Message = "Simulated transcript event.",
                    TranscriptText = clientEnvelope.SimulatedText ?? string.Empty,
                    IsFinal = clientEnvelope.IsFinalChunk ?? true
                });
                break;

            default:
                await SendAsync(socket, new ServerEnvelope
                {
                    Type = "error",
                    SessionId = currentSessionId,
                    Message = $"Unsupported message type: {clientEnvelope.Type}"
                });
                break;
        }
    }
});

app.Run();

static async Task<string> ReadMessageAsync(WebSocket socket, byte[] buffer, WebSocketReceiveResult initialResult, CancellationToken cancellationToken)
{
    using var stream = new MemoryStream();
    stream.Write(buffer, 0, initialResult.Count);

    var result = initialResult;
    while (!result.EndOfMessage)
    {
        result = await socket.ReceiveAsync(buffer, cancellationToken);
        stream.Write(buffer, 0, result.Count);
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}

static async Task SendAsync(WebSocket socket, ServerEnvelope envelope)
{
    var json = JsonSerializer.Serialize(envelope, JsonOptions.Default);
    var bytes = Encoding.UTF8.GetBytes(json);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}

sealed class TranscriptionSessionStore
{
    private readonly ConcurrentDictionary<string, TranscriptionSessionSnapshot> sessions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<TranscriptionSessionSnapshot> GetAll() => sessions.Values.OrderBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase).ToArray();

    public TranscriptionSessionSnapshot StartOrUpdate(string sessionId, string? encoding, int? sampleRate, int? channels, long receivedBytes)
    {
        return sessions.AddOrUpdate(
            sessionId,
            _ => new TranscriptionSessionSnapshot
            {
                SessionId = sessionId,
                ConnectedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ReceivedChunkCount = receivedBytes > 0 ? 1 : 0,
                ReceivedAudioBytes = receivedBytes,
                LastEncoding = encoding ?? string.Empty,
                LastSampleRate = sampleRate,
                LastChannels = channels
            },
            (_, existing) =>
            {
                existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
                existing.LastEncoding = encoding ?? existing.LastEncoding;
                existing.LastSampleRate = sampleRate ?? existing.LastSampleRate;
                existing.LastChannels = channels ?? existing.LastChannels;
                if (receivedBytes > 0)
                {
                    existing.ReceivedChunkCount += 1;
                    existing.ReceivedAudioBytes += receivedBytes;
                }

                return existing;
            });
    }
}

static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
