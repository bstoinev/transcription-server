namespace AIO.Transcription.Server;

public sealed class HostingOptions
{
    public const string SectionName = "Hosting";

    public string Urls { get; set; } = "http://127.0.0.1:43071";
}
