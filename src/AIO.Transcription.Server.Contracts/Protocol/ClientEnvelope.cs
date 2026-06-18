namespace AIO.Transcription.Server.Contracts.Protocol;

public sealed class ClientEnvelope
{
    public string Type { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string? AudioBase64 { get; set; }
    public string? Encoding { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public string? ModelType { get; set; }
    public string? Prompt { get; set; }
    public string? Language { get; set; }
    public bool? EnableLanguageDetection { get; set; }
    public bool? IsFinalChunk { get; set; }
    public string? SimulatedText { get; set; }
}
