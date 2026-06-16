namespace AIO.Transcription.Server.Contracts.Protocol;

public sealed class TranscriptionCapabilities
{
    public string DefaultModelType { get; set; } = string.Empty;
    public bool SupportsBinaryAudio { get; set; }
    public bool SupportsSessionModelSelection { get; set; }
    public int MaxConcurrentSessions { get; set; } = 1;
    public TranscriptionModelInfo[] AvailableModels { get; set; } = [];
}

public sealed class TranscriptionModelInfo
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool EnglishOnly { get; set; }
    public bool IsDownloaded { get; set; }
}
