namespace AIO.Transcription.Server.Contracts.Protocol;

public sealed class ServerEnvelope
{
    public string Type { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset SentAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? ModelType { get; set; }
    public string? TranscriptText { get; set; }
    public bool? IsFinal { get; set; }
    public int? ReceivedChunkCount { get; set; }
    public long? ReceivedAudioBytes { get; set; }
    public TranscriptionCapabilities? Capabilities { get; set; }
}
