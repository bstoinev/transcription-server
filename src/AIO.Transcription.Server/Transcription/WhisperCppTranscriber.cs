using System.Text;
using AIO.Transcription.Server.Logging;
using LogMachina;
using Whisper.net;
using Whisper.net.Ggml;

namespace AIO.Transcription.Server.Transcription;

public sealed class WhisperCppTranscriber : IWaveTranscriber, IDisposable
{
    private readonly WhisperTranscriberOptions options;
    private readonly ILogMachina<WhisperCppTranscriber> log;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private WhisperFactory? factory;
    private string? resolvedModelPath;

    public WhisperCppTranscriber(WhisperTranscriberOptions options, ILogMachina<WhisperCppTranscriber> log)
    {
        this.options = options;
        this.log = log;
    }

    public async Task<string> TranscribeWaveAsync(WaveTranscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.WaveBytes);
        if (request.WaveBytes.Length == 0)
        {
            return string.Empty;
        }

        var currentFactory = await GetFactoryAsync(cancellationToken);
        var builder = currentFactory.CreateBuilder();
        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            builder = builder.WithPrompt(request.Prompt);
        }

        if (request.EnableLanguageDetection || string.Equals(request.Language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            builder = builder.WithLanguageDetection();
        }
        else if (!string.IsNullOrWhiteSpace(request.Language))
        {
            builder = builder.WithLanguage(request.Language);
        }

        await using var processor = builder.Build();
        using var stream = new MemoryStream(request.WaveBytes, writable: false);
        var transcript = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(stream, cancellationToken))
        {
            var text = segment.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (transcript.Length > 0)
            {
                transcript.Append(' ');
            }

            transcript.Append(text);
        }

        var result = transcript.ToString().Trim();
        log.Info(
            $"Completed whisper transcription. WaveBytes={request.WaveBytes.Length} TranscriptChars={result.Length} Language={request.Language ?? "<auto>"} PromptChars={request.Prompt?.Length ?? 0} DetectLanguage={request.EnableLanguageDetection}");
        return result;
    }

    public void Dispose()
    {
        log.Info("Disposing whisper transcriber resources.");
        factory?.Dispose();
        initializationLock.Dispose();
    }

    private async Task<string> EnsureModelPathAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(resolvedModelPath) && File.Exists(resolvedModelPath))
        {
            return resolvedModelPath;
        }

        var modelDirectory = ResolveModelDirectory();
        Directory.CreateDirectory(modelDirectory);
        var (ggmlType, modelStem) = ResolveModelDefinition(options.ModelType);
        var targetPath = ResolveConfiguredModelPath(modelDirectory, modelStem);
        if (!File.Exists(targetPath))
        {
            if (!options.AutoDownloadModel)
            {
                throw new FileNotFoundException(
                    $"Whisper model was not found at {targetPath}, and auto-download is disabled.");
            }

            log.Info($"Downloading whisper model. ModelType={ggmlType} TargetPath={targetPath}");
            await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                ggmlType,
                QuantizationType.NoQuantization,
                cancellationToken);
            await using var fileStream = File.Create(targetPath);
            await modelStream.CopyToAsync(fileStream, cancellationToken);
        }

        resolvedModelPath = targetPath;
        log.Info($"Resolved whisper model path. ModelPath={resolvedModelPath}");
        return resolvedModelPath;
    }

    private string ResolveModelDirectory()
    {
        if (string.IsNullOrWhiteSpace(options.ModelPath))
        {
            return Path.Combine(AppContext.BaseDirectory, "models");
        }

        var configuredPath = options.ModelPath.Trim();
        if (Directory.Exists(configuredPath))
        {
            return configuredPath;
        }

        if (File.Exists(configuredPath))
        {
            return Path.GetDirectoryName(configuredPath)
                ?? throw new InvalidOperationException($"Whisper model path '{configuredPath}' does not have a valid parent directory.");
        }

        if (Path.HasExtension(configuredPath))
        {
            var parentDirectory = Path.GetDirectoryName(configuredPath);
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                throw new FileNotFoundException($"Whisper model was not found at {configuredPath}");
            }

            Directory.CreateDirectory(parentDirectory);
            return parentDirectory;
        }

        Directory.CreateDirectory(configuredPath);
        return configuredPath;
    }

    private string ResolveConfiguredModelPath(string modelDirectory, string modelStem)
    {
        if (!string.IsNullOrWhiteSpace(options.ModelPath))
        {
            var configuredPath = options.ModelPath.Trim();
            if (File.Exists(configuredPath))
            {
                return configuredPath;
            }

            if (Path.HasExtension(configuredPath))
            {
                return configuredPath;
            }
        }

        return Path.Combine(modelDirectory, $"ggml-{modelStem}.bin");
    }

    private static (GgmlType GgmlType, string FileStem) ResolveModelDefinition(string? modelType)
    {
        if (string.IsNullOrWhiteSpace(modelType))
        {
            return (GgmlType.BaseEn, "base.en");
        }

        var normalized = modelType.Trim();
        if (Enum.TryParse<GgmlType>(normalized, true, out var enumParsed))
        {
            return (enumParsed, GetModelFileStem(enumParsed));
        }

        normalized = normalized.ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "tiny.en" => (GgmlType.TinyEn, "tiny.en"),
            "base.en" => (GgmlType.BaseEn, "base.en"),
            "small.en" => (GgmlType.SmallEn, "small.en"),
            "medium.en" => (GgmlType.MediumEn, "medium.en"),
            "tiny" => (GgmlType.Tiny, "tiny"),
            "base" => (GgmlType.Base, "base"),
            "small" => (GgmlType.Small, "small"),
            "medium" => (GgmlType.Medium, "medium"),
            "large-v1" => (GgmlType.LargeV1, "large-v1"),
            "large-v2" => (GgmlType.LargeV2, "large-v2"),
            "large-v3" => (GgmlType.LargeV3, "large-v3"),
            "large-v3-turbo" => (GgmlType.LargeV3Turbo, "large-v3-turbo"),
            _ => throw new InvalidOperationException(
                $"Unsupported Transcription:ModelType '{modelType}'. Use the whisper.cpp stem that comes after 'ggml-' in the filename, such as 'base.en', 'medium.en', 'base', 'medium', 'large-v3', or 'large-v3-turbo'.")
        };
    }

    private static string GetModelFileStem(GgmlType ggmlType)
    {
        return ggmlType switch
        {
            GgmlType.TinyEn => "tiny.en",
            GgmlType.BaseEn => "base.en",
            GgmlType.SmallEn => "small.en",
            GgmlType.MediumEn => "medium.en",
            GgmlType.LargeV1 => "large-v1",
            GgmlType.LargeV2 => "large-v2",
            GgmlType.LargeV3 => "large-v3",
            GgmlType.LargeV3Turbo => "large-v3-turbo",
            _ => ggmlType.ToString().ToLowerInvariant().Replace('_', '-')
        };
    }

    private async Task<WhisperFactory> GetFactoryAsync(CancellationToken cancellationToken)
    {
        if (factory is not null)
        {
            return factory;
        }

        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (factory is not null)
            {
                return factory;
            }

            var modelPath = await EnsureModelPathAsync(cancellationToken);
            var factoryOptions = new WhisperFactoryOptions
            {
                UseGpu = options.UseGpu,
                GpuDevice = options.GpuDevice,
                UseFlashAttention = options.UseFlashAttention
            };

            log.Info(
                $"Initializing whisper factory. ModelPath={modelPath} UseGpu={factoryOptions.UseGpu} GpuDevice={factoryOptions.GpuDevice} UseFlashAttention={factoryOptions.UseFlashAttention}");
            factory = WhisperFactory.FromPath(modelPath, factoryOptions);
            var runtimeInfo = WhisperFactory.GetRuntimeInfo() ?? string.Empty;
            log.Info($"Whisper runtime info: {runtimeInfo}");
            EnforceRuntimeExpectation(runtimeInfo);
            return factory;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private void EnforceRuntimeExpectation(string runtimeInfo)
    {
        if (!options.UseGpu)
        {
            return;
        }

        if (options.AllowCpuFallback is null)
        {
            var message =
                "GPU transcription is enabled, but Transcription:AllowCpuFallback is not configured. Set it explicitly to true or false.";
            log.Error(message);
            throw new InvalidOperationException(message);
        }

        if (runtimeInfo.Contains("CUDA", StringComparison.OrdinalIgnoreCase))
        {
            log.Info($"CUDA runtime selected successfully. RuntimeInfo={runtimeInfo}");
            return;
        }

        if (options.AllowCpuFallback.Value)
        {
            log.Warn(
                $"GPU was requested but CUDA runtime was not selected. Falling back to CPU because Transcription:AllowCpuFallback=true. RuntimeInfo={runtimeInfo}");
            return;
        }

        var failureMessage =
            $"GPU was requested but CUDA runtime was not selected, and Transcription:AllowCpuFallback=false. RuntimeInfo={runtimeInfo}";
        log.Error(failureMessage);
        throw new InvalidOperationException(failureMessage);
    }
}
