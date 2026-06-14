namespace AIO.Transcription.Server.Transcription;

public interface IWaveTranscriber
{
    Task<string> TranscribeWaveAsync(byte[] waveBytes, CancellationToken cancellationToken);
}
