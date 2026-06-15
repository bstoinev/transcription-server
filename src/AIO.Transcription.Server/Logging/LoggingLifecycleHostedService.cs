using LogMachina;
using Microsoft.Extensions.Hosting;
using NLog;

namespace AIO.Transcription.Server.Logging;

public sealed class LoggingLifecycleHostedService : IHostedService
{
    private readonly ILogMachina<LoggingLifecycleHostedService> log;
    private readonly IHostEnvironment environment;
    private UnhandledExceptionEventHandler? unhandledExceptionHandler;
    private EventHandler<UnobservedTaskExceptionEventArgs>? unobservedTaskExceptionHandler;

    public LoggingLifecycleHostedService(ILogMachina<LoggingLifecycleHostedService> log, IHostEnvironment environment)
    {
        this.log = log;
        this.environment = environment;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        unhandledExceptionHandler = (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                log.Error($"Unhandled domain exception. IsTerminating={args.IsTerminating}", ex);
                return;
            }

            log.Error($"Unhandled domain exception. IsTerminating={args.IsTerminating}. ExceptionObject={args.ExceptionObject}");
        };

        unobservedTaskExceptionHandler = (_, args) =>
        {
            log.Error("Unobserved task exception.", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += unhandledExceptionHandler;
        TaskScheduler.UnobservedTaskException += unobservedTaskExceptionHandler;

        log.Info(
            $"Logging initialized. Environment={environment.EnvironmentName} ContentRoot={environment.ContentRootPath} BaseDirectory={AppContext.BaseDirectory} ProcessId={Environment.ProcessId}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (unhandledExceptionHandler is not null)
        {
            AppDomain.CurrentDomain.UnhandledException -= unhandledExceptionHandler;
        }

        if (unobservedTaskExceptionHandler is not null)
        {
            TaskScheduler.UnobservedTaskException -= unobservedTaskExceptionHandler;
        }

        log.Info("Application stopping. Flushing LogMachina/NLog pipeline.");
        LogManager.Flush(TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }
}
