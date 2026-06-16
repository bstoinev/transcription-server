namespace AIO.Transcription.Server.Transcription;

public interface IWaveTranscriber
{
    Task<string> TranscribeWaveAsync(WaveTranscriptionRequest request, CancellationToken cancellationToken);
}
