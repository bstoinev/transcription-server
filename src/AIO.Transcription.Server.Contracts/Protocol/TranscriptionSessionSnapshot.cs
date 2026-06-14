namespace AIO.Transcription.Server.Contracts.Protocol;

public sealed class TranscriptionSessionSnapshot
{
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset ConnectedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int ReceivedChunkCount { get; set; }
    public long ReceivedAudioBytes { get; set; }
    public string LastEncoding { get; set; } = string.Empty;
    public int? LastSampleRate { get; set; }
    public int? LastChannels { get; set; }
}
