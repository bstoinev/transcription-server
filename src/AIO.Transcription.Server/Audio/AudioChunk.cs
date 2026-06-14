namespace AIO.Transcription.Server.Audio;

public sealed record AudioChunk(
    byte[] Buffer,
    int BytesRecorded,
    int SampleRate,
    int Channels,
    string Encoding,
    DateTimeOffset TimestampUtc);
