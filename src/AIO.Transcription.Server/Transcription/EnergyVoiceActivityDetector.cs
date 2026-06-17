namespace AIO.Transcription.Server.Transcription;

public sealed class EnergyVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly double threshold;

    public EnergyVoiceActivityDetector(double threshold)
    {
        if (threshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        this.threshold = threshold;
    }

    public bool IsSpeech(ReadOnlySpan<float> monoSamples)
    {
        if (monoSamples.Length == 0)
        {
            return false;
        }

        var squareSum = 0.0;
        for (var index = 0; index < monoSamples.Length; index += 1)
        {
            squareSum += monoSamples[index] * monoSamples[index];
        }

        var rms = Math.Sqrt(squareSum / monoSamples.Length);
        return rms >= threshold;
    }
}
