using AIO.Transcription.Server.Transcription;
using LogMachina;

namespace AIO.Transcription.Server.Logging;

public sealed class TranscriptionWarmupHostedService : IHostedService
{
    private readonly IWaveTranscriber transcriber;
    private readonly WhisperTranscriberOptions options;
    private readonly ILogMachina<TranscriptionWarmupHostedService> log;

    public TranscriptionWarmupHostedService(
        IWaveTranscriber transcriber,
        WhisperTranscriberOptions options,
        ILogMachina<TranscriptionWarmupHostedService> log)
    {
        this.transcriber = transcriber;
        this.options = options;
        this.log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.WarmupOnStartup)
        {
            log.Info("Transcription warmup is disabled.");
            return;
        }

        log.Info("Starting transcription warmup.");
        await transcriber.WarmUpAsync(cancellationToken);
        log.Info("Completed transcription warmup.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
