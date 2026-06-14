using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace AIO.Transcription.Server.Transcription;

public sealed class WhisperCppTranscriber : IWaveTranscriber, IDisposable
{
    private readonly WhisperTranscriberOptions options;
    private readonly ILogger<WhisperCppTranscriber> logger;
    private readonly SemaphoreSlim modelLock = new(1, 1);
    private WhisperFactory? factory;
    private string? resolvedModelPath;

    public WhisperCppTranscriber(WhisperTranscriberOptions options, ILogger<WhisperCppTranscriber> logger)
    {
        this.options = options;
        this.logger = logger;
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
            factory ??= WhisperFactory.FromPath(modelPath);
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

            return transcript.ToString().Trim();
        }
        finally
        {
            modelLock.Release();
        }
    }

    public void Dispose()
    {
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
            logger.LogInformation("Downloading whisper.cpp model {ModelType} to {TargetPath}", ggmlType, targetPath);
            await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                ggmlType,
                QuantizationType.NoQuantization,
                cancellationToken);
            await using var fileStream = File.Create(targetPath);
            await modelStream.CopyToAsync(fileStream, cancellationToken);
        }

        resolvedModelPath = targetPath;
        return resolvedModelPath;
    }
}
