namespace AIO.Transcription.Server.Transcription;

public sealed class WhisperTranscriberOptions
{
    public string? ModelPath { get; set; }
    public bool AutoDownloadModel { get; set; } = true;
    public string ModelType { get; set; } = "Tiny";
    public int TargetSampleRate { get; set; } = 16000;
    public int MinimumWindowMilliseconds { get; set; } = 3500;
    public bool BoundaryDetectionEnabled { get; set; } = true;
    public double BoundaryDetectionRmsThreshold { get; set; } = 0.015;
    public int BoundarySilenceMilliseconds { get; set; } = 700;
    public int MinimumSpeechMilliseconds { get; set; } = 250;
    public int MaximumSegmentMilliseconds { get; set; } = 15000;
}
