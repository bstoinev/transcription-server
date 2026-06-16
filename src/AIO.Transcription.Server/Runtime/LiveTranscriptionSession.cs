using System.Threading.Channels;
using AIO.Transcription.Server.Audio;
using AIO.Transcription.Server.Contracts.Protocol;
using AIO.Transcription.Server.Logging;
using AIO.Transcription.Server.Transcription;
using LogMachina;

namespace AIO.Transcription.Server.Runtime;

public sealed class LiveTranscriptionSession : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Channel<AudioChunk> inputChannel = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Channel<ServerEnvelope> updatesChannel = Channel.CreateUnbounded<ServerEnvelope>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = true
    });
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly IWaveTranscriber transcriber;
    private readonly WhisperTranscriberOptions options;
    private readonly ILogMachina<LiveTranscriptionSession> log;
    private readonly TranscriptionSessionSnapshot snapshot;
    private readonly Task processingTask;
    private string lastTranscriptSegment = string.Empty;
    private string transcriptPromptContext = string.Empty;
    private bool acceptingAudio = true;
    private bool flushPendingAudioOnCompletion;
    private Exception? processingFailure;
    private bool failureReported;

    public LiveTranscriptionSession(string sessionId, IWaveTranscriber transcriber, WhisperTranscriberOptions options, ILogMachina<LiveTranscriptionSession> log)
    {
        SessionId = sessionId;
        ModelType = WhisperModelCatalog.GetEffectiveConfiguredModelType(options);
        this.transcriber = transcriber;
        this.options = options;
        this.log = log;
        snapshot = new TranscriptionSessionSnapshot
        {
            SessionId = sessionId,
            ConnectedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        processingTask = ProcessChunksAsync();
        this.log.Info($"Initialized live transcription session. SessionId={sessionId} ModelType={ModelType} Language={options.Language ?? "<auto>"} EnableLanguageDetection={options.EnableLanguageDetection}");
    }

    public string SessionId { get; }
    public string ModelType { get; }

    public async Task AddAudioChunkAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        EnsureSessionIsAcceptingAudio();
        await inputChannel.Writer.WriteAsync(chunk, cancellationToken);

        lock (sync)
        {
            if (snapshot.ReceivedChunkCount > 0 &&
                (!string.Equals(snapshot.LastEncoding, chunk.Encoding, StringComparison.OrdinalIgnoreCase) ||
                 snapshot.LastSampleRate != chunk.SampleRate ||
                 snapshot.LastChannels != chunk.Channels))
            {
                log.Warn(
                    $"Audio format changed within active session. SessionId={SessionId} PreviousEncoding={snapshot.LastEncoding} PreviousSampleRate={snapshot.LastSampleRate} PreviousChannels={snapshot.LastChannels} NewEncoding={chunk.Encoding} NewSampleRate={chunk.SampleRate} NewChannels={chunk.Channels}");
            }

            snapshot.UpdatedAtUtc = DateTimeOffset.UtcNow;
            snapshot.ReceivedChunkCount += 1;
            snapshot.ReceivedAudioBytes += chunk.BytesRecorded;
            snapshot.LastEncoding = chunk.Encoding;
            snapshot.LastSampleRate = chunk.SampleRate;
            snapshot.LastChannels = chunk.Channels;
        }
    }

    public IAsyncEnumerable<ServerEnvelope> ReadUpdatesAsync(CancellationToken cancellationToken)
    {
        return updatesChannel.Reader.ReadAllAsync(cancellationToken);
    }

    public async Task CompleteAsync(bool flushPendingAudio, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            flushPendingAudioOnCompletion |= flushPendingAudio;
            acceptingAudio = false;
        }

        inputChannel.Writer.TryComplete();
        await processingTask.WaitAsync(cancellationToken);
    }

    public ServerEnvelope BuildEndedEnvelope()
    {
        var snapshotCopy = CreateSnapshot();
        return new ServerEnvelope
        {
            Type = "session-ended",
            SessionId = SessionId,
            Message = "Session ended.",
            ModelType = ModelType,
            ReceivedChunkCount = snapshotCopy.ReceivedChunkCount,
            ReceivedAudioBytes = snapshotCopy.ReceivedAudioBytes,
        };
    }

    public TranscriptionSessionSnapshot CreateSnapshot()
    {
        lock (sync)
        {
            return CloneSnapshot(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            acceptingAudio = false;
        }

        lifetimeCts.Cancel();
        inputChannel.Writer.TryComplete();

        try
        {
            await processingTask;
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
        }
        finally
        {
            switch (transcriber)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        lifetimeCts.Dispose();
    }

    private void EnsureSessionIsAcceptingAudio()
    {
        lock (sync)
        {
            if (processingFailure is not null)
            {
                if (!failureReported)
                {
                    failureReported = true;
                    throw new InvalidOperationException($"Session {SessionId} can no longer process audio.", processingFailure);
                }

                throw new InvalidOperationException($"Session {SessionId} can no longer process audio because transcription already failed.");
            }

            if (!acceptingAudio)
            {
                throw new InvalidOperationException($"Session {SessionId} is no longer accepting audio.");
            }
        }
    }

    private async Task ProcessChunksAsync()
    {
        var pendingChunks = new List<AudioChunk>();
        var pendingMilliseconds = 0.0;

        try
        {
            await foreach (var chunk in inputChannel.Reader.ReadAllAsync(lifetimeCts.Token))
            {
                pendingChunks.Add(chunk);
                pendingMilliseconds += WavePcm16Writer.EstimateChunkMilliseconds(chunk);

                if (pendingMilliseconds < options.BufferWindowMillisecondsResolved)
                {
                    continue;
                }

                await ProcessWindowAsync(pendingChunks, pendingMilliseconds, isFinal: false, lifetimeCts.Token);
                pendingChunks.Clear();
                pendingMilliseconds = 0.0;
            }

            if (pendingChunks.Count > 0 && ShouldFlushPendingAudio())
            {
                await ProcessWindowAsync(pendingChunks, pendingMilliseconds, isFinal: true, lifetimeCts.Token);
            }
            else if (pendingChunks.Count > 0)
            {
                log.Info(
                    $"Discarded trailing buffered audio during session shutdown. SessionId={SessionId} PendingChunkCount={pendingChunks.Count} PendingMilliseconds={pendingMilliseconds:F2}");
            }
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
            log.Info($"Stopped background transcription processing. SessionId={SessionId}");
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                processingFailure = ex;
                acceptingAudio = false;
            }

            log.Error($"Background transcription processing failed. SessionId={SessionId}", ex);
            updatesChannel.Writer.TryWrite(new ServerEnvelope
            {
                Type = "error",
                SessionId = SessionId,
                Message = $"Session transcription failed: {ex.Message}"
            });
        }
        finally
        {
            updatesChannel.Writer.TryComplete();
        }
    }

    private async Task ProcessWindowAsync(List<AudioChunk> pendingChunks, double pendingMilliseconds, bool isFinal, CancellationToken cancellationToken)
    {
        var window = pendingChunks.ToArray();
        log.Info(
            $"Transcription window reached. SessionId={SessionId} WindowChunkCount={window.Length} PendingMilliseconds={pendingMilliseconds:F2} BufferWindowMilliseconds={options.BufferWindowMillisecondsResolved} TargetSampleRate={options.TargetSampleRate} IsFinal={isFinal}");

        var waveBytes = WavePcm16Writer.WriteWaveFile(window, options.TargetSampleRate);
        var request = new WaveTranscriptionRequest(
            waveBytes,
            GetPromptContext(),
            options.Language,
            options.EnableLanguageDetection);
        var text = (await transcriber.TranscribeWaveAsync(request, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (string.Equals(lastTranscriptSegment, text, StringComparison.OrdinalIgnoreCase))
        {
            log.Trace($"Skipping duplicate transcript segment. SessionId={SessionId}");
            return;
        }

        lastTranscriptSegment = text;
        AppendPromptContext(text);
        log.Info($"Transcript segment updated. SessionId={SessionId} TranscriptChars={text.Length} IsFinal={isFinal}");

        updatesChannel.Writer.TryWrite(new ServerEnvelope
        {
            Type = "transcript",
            SessionId = SessionId,
            Message = "Transcript updated.",
            ModelType = ModelType,
            TranscriptText = text,
            IsFinal = isFinal,
            ReceivedChunkCount = CreateSnapshot().ReceivedChunkCount,
            ReceivedAudioBytes = CreateSnapshot().ReceivedAudioBytes,
        });
    }

    private string? GetPromptContext()
    {
        lock (sync)
        {
            return string.IsNullOrWhiteSpace(transcriptPromptContext) ? null : transcriptPromptContext;
        }
    }

    private void AppendPromptContext(string text)
    {
        if (options.PromptContextCharactersResolved <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (sync)
        {
            transcriptPromptContext = string.IsNullOrWhiteSpace(transcriptPromptContext)
                ? text
                : $"{transcriptPromptContext} {text}".Trim();

            if (transcriptPromptContext.Length > options.PromptContextCharactersResolved)
            {
                transcriptPromptContext = transcriptPromptContext[^options.PromptContextCharactersResolved..].TrimStart();
            }
        }
    }

    private bool ShouldFlushPendingAudio()
    {
        lock (sync)
        {
            return flushPendingAudioOnCompletion;
        }
    }

    private static TranscriptionSessionSnapshot CloneSnapshot(TranscriptionSessionSnapshot source)
    {
        return new TranscriptionSessionSnapshot
        {
            SessionId = source.SessionId,
            ConnectedAtUtc = source.ConnectedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            ReceivedChunkCount = source.ReceivedChunkCount,
            ReceivedAudioBytes = source.ReceivedAudioBytes,
            LastEncoding = source.LastEncoding,
            LastSampleRate = source.LastSampleRate,
            LastChannels = source.LastChannels,
        };
    }
}
