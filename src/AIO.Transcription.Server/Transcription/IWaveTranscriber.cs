namespace AIO.Transcription.Server.Transcription;

public interface IWaveTranscriber
{
    Task WarmUpAsync(CancellationToken cancellationToken);
    Task<string> TranscribeWaveAsync(WaveTranscriptionRequest request, CancellationToken cancellationToken);
}
