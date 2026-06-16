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
    private readonly SemaphoreSlim modelLock = new(1, 1);
    private WhisperFactory? factory;
    private string? resolvedModelPath;

    public WhisperCppTranscriber(WhisperTranscriberOptions options, ILogMachina<WhisperCppTranscriber> log)
    {
        this.options = options;
        this.log = log;
    }

    public async Task<string> TranscribeWaveAsync(byte[] waveBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waveBytes);
        if (waveBytes.Length == 0)
        {
            return string.Empty;
        }

        await modelLock.WaitAsync(cancellationToken);
        try
        {
            var modelPath = await EnsureModelPathAsync(cancellationToken);
            if (factory is null)
            {
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
            }

            await using var processor = factory.CreateBuilder().WithLanguage("auto").Build();
            using var stream = new MemoryStream(waveBytes, writable: false);
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
            log.Info($"Completed whisper transcription. WaveBytes={waveBytes.Length} TranscriptChars={result.Length}");
            return result;
        }
        finally
        {
            modelLock.Release();
        }
    }

    public void Dispose()
    {
        log.Info("Disposing whisper transcriber resources.");
        factory?.Dispose();
        modelLock.Dispose();
    }

    private async Task<string> EnsureModelPathAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(resolvedModelPath) && File.Exists(resolvedModelPath))
        {
            return resolvedModelPath;
        }

        if (!string.IsNullOrWhiteSpace(options.ModelPath))
        {
            if (!File.Exists(options.ModelPath))
            {
                throw new FileNotFoundException($"Whisper model was not found at {options.ModelPath}");
            }

            resolvedModelPath = options.ModelPath;
            log.Info($"Using configured whisper model path. ModelPath={resolvedModelPath}");
            return resolvedModelPath;
        }

        if (!options.AutoDownloadModel)
        {
            throw new InvalidOperationException("No whisper model path was configured and auto-download is disabled.");
        }

        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "models");
        Directory.CreateDirectory(modelDirectory);
        var ggmlType = Enum.TryParse<GgmlType>(options.ModelType, true, out var parsed)
            ? parsed
            : GgmlType.TinyEn;
        var extension = ggmlType.ToString().ToLowerInvariant().Replace('_', '-');
        var targetPath = Path.Combine(modelDirectory, $"ggml-{extension}.bin");
        if (!File.Exists(targetPath))
        {
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
