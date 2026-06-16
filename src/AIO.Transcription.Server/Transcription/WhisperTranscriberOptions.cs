namespace AIO.Transcription.Server.Transcription;

public sealed class WhisperTranscriberOptions
{
    private const int DefaultBufferWindowMilliseconds = 1000;

    public string? ModelPath { get; set; }
    public bool AutoDownloadModel { get; set; } = true;
    public string ModelType { get; set; } = "TinyEn";
    public int TargetSampleRate { get; set; } = 16000;
    public int? BufferWindowMilliseconds { get; set; } = DefaultBufferWindowMilliseconds;
    public int? MinimumWindowMilliseconds { get; set; }
    public bool UseGpu { get; set; } = true;
    public bool? AllowCpuFallback { get; set; }
    public int GpuDevice { get; set; }
    public bool UseFlashAttention { get; set; }

    public int BufferWindowMillisecondsResolved =>
        NormalizeBufferWindowMilliseconds(BufferWindowMilliseconds)
        ?? NormalizeBufferWindowMilliseconds(MinimumWindowMilliseconds)
        ?? DefaultBufferWindowMilliseconds;

    private static int? NormalizeBufferWindowMilliseconds(int? value)
    {
        if (value is null)
        {
            return null;
        }

        return Math.Max(100, value.Value);
    }
}
