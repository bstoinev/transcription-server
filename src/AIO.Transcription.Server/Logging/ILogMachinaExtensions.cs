using LogMachina;

namespace AIO.Transcription.Server.Logging;

public static class ILogMachinaExtensions
{
    public static void Error(this ILogMachina log, string message, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(ex);

        log.Error(message);
        log.Error(ex);
    }
}
