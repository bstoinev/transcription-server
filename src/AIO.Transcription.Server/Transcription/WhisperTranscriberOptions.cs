namespace AIO.Transcription.Server.Transcription;

public sealed class WhisperTranscriberOptions
{
    public string? ModelPath { get; set; }
    public bool AutoDownloadModel { get; set; } = true;
    public string ModelType { get; set; } = "TinyEn";
    public int TargetSampleRate { get; set; } = 16000;
    public int MinimumWindowMilliseconds { get; set; } = 3500;
}
