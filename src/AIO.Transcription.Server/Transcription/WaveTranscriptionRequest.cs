namespace AIO.Transcription.Server.Transcription;

public sealed record WaveTranscriptionRequest(
    byte[] WaveBytes,
    string? Prompt,
    string? Language,
    bool EnableLanguageDetection);
