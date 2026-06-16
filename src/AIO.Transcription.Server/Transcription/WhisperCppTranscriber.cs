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

        var modelDirectory = WhisperModelCatalog.ResolveModelDirectory(options);
        Directory.CreateDirectory(modelDirectory);
        var modelDefinition = WhisperModelCatalog.Resolve(options.ModelType);
        var targetPath = WhisperModelCatalog.ResolveModelPath(options, modelDefinition.Id);
        if (!File.Exists(targetPath))
        {
            if (!options.AutoDownloadModel)
            {
                throw new FileNotFoundException(
                    $"Whisper model was not found at {targetPath}, and auto-download is disabled.");
            }

            log.Info($"Downloading whisper model. ModelType={modelDefinition.Id} TargetPath={targetPath}");
            await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                modelDefinition.GgmlType,
                QuantizationType.NoQuantization,
                cancellationToken);
            await using var fileStream = File.Create(targetPath);
            await modelStream.CopyToAsync(fileStream, cancellationToken);
        }

        resolvedModelPath = targetPath;
        log.Info($"Resolved whisper model path. ModelPath={resolvedModelPath}");
        return resolvedModelPath;
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
