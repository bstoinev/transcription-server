namespace AIO.Transcription.Server.Transcription;

public sealed class WhisperTranscriberOptions
{
    private const int DefaultBufferWindowMilliseconds = 1000;
    private const int DefaultPromptContextCharacters = 256;

    public string? ModelPath { get; set; }
    public bool AutoDownloadModel { get; set; } = true;
    public string ModelType { get; set; } = "base.en";
    public int TargetSampleRate { get; set; } = 16000;
    public int? BufferWindowMilliseconds { get; set; } = DefaultBufferWindowMilliseconds;
    public int? MinimumWindowMilliseconds { get; set; }
    public bool UseGpu { get; set; } = true;
    public bool? AllowCpuFallback { get; set; }
    public int GpuDevice { get; set; }
    public bool UseFlashAttention { get; set; }
    public string? Language { get; set; } = "en";
    public bool EnableLanguageDetection { get; set; }
    public int PromptContextCharacters { get; set; } = DefaultPromptContextCharacters;

    public WhisperTranscriberOptions Clone()
    {
        return new WhisperTranscriberOptions
        {
            ModelPath = ModelPath,
            AutoDownloadModel = AutoDownloadModel,
            ModelType = ModelType,
            TargetSampleRate = TargetSampleRate,
            BufferWindowMilliseconds = BufferWindowMilliseconds,
            MinimumWindowMilliseconds = MinimumWindowMilliseconds,
            UseGpu = UseGpu,
            AllowCpuFallback = AllowCpuFallback,
            GpuDevice = GpuDevice,
            UseFlashAttention = UseFlashAttention,
            Language = Language,
            EnableLanguageDetection = EnableLanguageDetection,
            PromptContextCharacters = PromptContextCharacters,
        };
    }

    public int BufferWindowMillisecondsResolved =>
        NormalizeBufferWindowMilliseconds(BufferWindowMilliseconds)
        ?? NormalizeBufferWindowMilliseconds(MinimumWindowMilliseconds)
        ?? DefaultBufferWindowMilliseconds;

    public int PromptContextCharactersResolved => Math.Max(0, PromptContextCharacters);

    private static int? NormalizeBufferWindowMilliseconds(int? value)
    {
        if (value is null)
        {
            return null;
        }

        return Math.Max(100, value.Value);
    }
}
