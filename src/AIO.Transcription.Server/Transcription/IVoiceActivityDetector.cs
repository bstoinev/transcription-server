namespace AIO.Transcription.Server.Transcription;

public interface IVoiceActivityDetector
{
    bool IsSpeech(ReadOnlySpan<float> monoSamples);
}
