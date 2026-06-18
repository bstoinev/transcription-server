namespace AIO.Transcription.Server.Transcription;

public sealed class WhisperTranscriberOptions
{
    private const int DefaultPromptContextCharacters = 256;

    public string? ModelPath { get; set; }
    public bool AutoDownloadModel { get; set; } = true;
    public string? ModelType { get; set; }
    public int TargetSampleRate { get; set; } = 16000;
    public int PartialUpdateIntervalMs { get; set; } = 750;
    public int PartialWindowMs { get; set; } = 12000;
    public int MinimumUtteranceMs { get; set; } = 1800;
    public int EndSilenceMs { get; set; } = 1000;
    public int MaxUtteranceMs { get; set; } = 30000;
    public int PreRollMs { get; set; } = 500;
    public int PostRollMs { get; set; } = 700;
    public int VadFrameMs { get; set; } = 20;
    public double VadEnergyThreshold { get; set; } = 0.015;
    public string TechnicalPrompt { get; set; } = string.Empty;
    public int? BufferWindowMilliseconds { get; set; }
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
            PartialUpdateIntervalMs = PartialUpdateIntervalMs,
            PartialWindowMs = PartialWindowMs,
            MinimumUtteranceMs = MinimumUtteranceMs,
            EndSilenceMs = EndSilenceMs,
            MaxUtteranceMs = MaxUtteranceMs,
            PreRollMs = PreRollMs,
            PostRollMs = PostRollMs,
            VadFrameMs = VadFrameMs,
            VadEnergyThreshold = VadEnergyThreshold,
            TechnicalPrompt = TechnicalPrompt,
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

    public int PromptContextCharactersResolved => Math.Max(0, PromptContextCharacters);

    public void Validate()
    {
        if (BufferWindowMilliseconds.HasValue || MinimumWindowMilliseconds.HasValue)
        {
            throw new InvalidOperationException(
                "Transcription:BufferWindowMilliseconds and Transcription:MinimumWindowMilliseconds are obsolete. Configure PartialUpdateIntervalMs, PartialWindowMs, MinimumUtteranceMs, EndSilenceMs, MaxUtteranceMs, PreRollMs, and PostRollMs instead.");
        }

        if (TargetSampleRate <= 0)
        {
            throw new InvalidOperationException("Transcription:TargetSampleRate must be greater than zero.");
        }

        if (PartialUpdateIntervalMs <= 0 || PartialWindowMs <= 0 || MinimumUtteranceMs <= 0 ||
            EndSilenceMs <= 0 || MaxUtteranceMs <= 0 || PreRollMs < 0 || PostRollMs < 0 ||
            VadFrameMs <= 0 || VadEnergyThreshold <= 0)
        {
            throw new InvalidOperationException("Live transcription timing and VAD settings must be positive, except PreRollMs and PostRollMs which may be zero.");
        }

        if (PartialUpdateIntervalMs > PartialWindowMs)
        {
            throw new InvalidOperationException("Transcription:PartialUpdateIntervalMs must be less than or equal to Transcription:PartialWindowMs.");
        }

        if (PreRollMs >= PartialWindowMs)
        {
            throw new InvalidOperationException("Transcription:PreRollMs must be less than Transcription:PartialWindowMs.");
        }

        if (PostRollMs >= EndSilenceMs)
        {
            throw new InvalidOperationException("Transcription:PostRollMs must be less than Transcription:EndSilenceMs.");
        }

        if (MinimumUtteranceMs > MaxUtteranceMs)
        {
            throw new InvalidOperationException("Transcription:MinimumUtteranceMs must be less than or equal to Transcription:MaxUtteranceMs.");
        }

        if (string.IsNullOrWhiteSpace(Language))
        {
            Language = "en";
        }

        if (!string.IsNullOrWhiteSpace(ModelType))
        {
            ModelType = ModelType.Trim();
        }
    }
}
