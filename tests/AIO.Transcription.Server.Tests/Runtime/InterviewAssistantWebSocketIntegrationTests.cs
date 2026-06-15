using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using InterviewAssistant.Core.Audio;
using InterviewAssistant.Core.Protocol;
using InterviewAssistant.Core.Services;
using Xunit;
using Xunit.Sdk;

namespace AIO.Transcription.Server.Tests.Runtime;

public sealed class InterviewAssistantWebSocketIntegrationTests
{
    [Fact]
    public async Task InterviewAssistantClient_WebSocketRoundTrip_ProducesTranscript()
    {
        var port = GetFreeTcpPort();
        await using var server = await TestServerHost.StartAsync(port, CancellationToken.None);

        using var readyClient = new HttpClient();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await server.WaitForReadyAsync(readyClient, timeoutCts.Token);

        var endpoint = new Uri($"ws://127.0.0.1:{port}/ws/transcribe");
        await using var client = new TranscriptionServerClient();
        var envelopes = new ConcurrentQueue<ServerEnvelope>();
        using var transcriptSignal = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        client.ServerEnvelopeReceived += (_, envelope) => envelopes.Enqueue(envelope);

        await client.ConnectAsync(endpoint, timeoutCts.Token);

        var sessionId = $"ws-integration-{Guid.NewGuid():N}";
        await client.StartSessionAsync(sessionId, selectedDevice: null, timeoutCts.Token);

        var sequence = 1;
        foreach (var chunk in ReadWaveChunksAsFloat32(Path.Combine(AppContext.BaseDirectory, "TestData", "jfk.wav"), 500))
        {
            await client.SendAudioChunkAsync(sessionId, sequence, chunk, timeoutCts.Token);
            sequence += 1;
        }

        await client.EndSessionAsync(sessionId, timeoutCts.Token);

        var transcript = await WaitForSessionEndAndCollectTranscriptAsync(envelopes, transcriptSignal.Token);
        Assert.Contains("Americans", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.True(transcript.Length >= 20, $"Expected a non-trivial transcript, got: '{transcript}'");
    }

    private static async Task<string> WaitForSessionEndAndCollectTranscriptAsync(ConcurrentQueue<ServerEnvelope> envelopes, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var hasEnded = envelopes.Any(x => x.Type == "session-ended");
            var transcript = string.Join(" ", envelopes
                .Where(x => x.Type == "transcript")
                .Select(x => x.TranscriptText)
                .Where(x => !string.IsNullOrWhiteSpace(x)));

            if (hasEnded && !string.IsNullOrWhiteSpace(transcript))
            {
                return transcript;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for transcript envelope over WebSocket.");
    }

    private static IEnumerable<AudioChunk> ReadWaveChunksAsFloat32(string wavePath, int chunkMilliseconds)
    {
        using var stream = File.OpenRead(wavePath);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        var riff = new string(reader.ReadChars(4));
        Assert.Equal("RIFF", riff);
        _ = reader.ReadInt32();
        var wave = new string(reader.ReadChars(4));
        Assert.Equal("WAVE", wave);

        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? pcmData = null;

        while (stream.Position < stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                var audioFormat = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadInt32();
                _ = reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
                if (chunkSize > 16)
                {
                    reader.ReadBytes(chunkSize - 16);
                }

                Assert.Equal(1, audioFormat);
            }
            else if (chunkId == "data")
            {
                pcmData = reader.ReadBytes(chunkSize);
            }
            else
            {
                reader.ReadBytes(chunkSize);
            }

            if ((chunkSize & 1) == 1)
            {
                reader.ReadByte();
            }
        }

        Assert.NotNull(pcmData);
        Assert.Equal(16, bitsPerSample);
        Assert.True(channels > 0);
        Assert.True(sampleRate > 0);

        var bytesPerFrame = channels * (bitsPerSample / 8);
        var framesPerChunk = sampleRate * chunkMilliseconds / 1000;
        var bytesPerChunk = framesPerChunk * bytesPerFrame;

        for (var offset = 0; offset < pcmData!.Length; offset += bytesPerChunk)
        {
            var count = Math.Min(bytesPerChunk, pcmData.Length - offset);
            var usableCount = count - (count % bytesPerFrame);
            var floatBytes = new byte[(usableCount / bytesPerFrame) * channels * sizeof(float)];

            for (var frameOffset = 0; frameOffset < usableCount; frameOffset += bytesPerFrame)
            {
                for (var channelIndex = 0; channelIndex < channels; channelIndex += 1)
                {
                    var sourceOffset = frameOffset + (channelIndex * sizeof(short));
                    var targetOffset = ((frameOffset / bytesPerFrame) * channels + channelIndex) * sizeof(float);
                    var sample = BitConverter.ToInt16(pcmData, offset + sourceOffset) / (float)short.MaxValue;
                    BitConverter.GetBytes(sample).CopyTo(floatBytes, targetOffset);
                }
            }

            yield return new AudioChunk(
                floatBytes,
                floatBytes.Length,
                sampleRate,
                channels,
                "f32le",
                "test-device",
                "test-device",
                DateTimeOffset.UtcNow);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class TestServerHost : IAsyncDisposable
    {
        private readonly Process process;
        private readonly int port;

        private TestServerHost(Process process, int port)
        {
            this.process = process;
            this.port = port;
        }

        public static Task<TestServerHost> StartAsync(int port, CancellationToken cancellationToken)
        {
            var serverDll = Path.Combine(AppContext.BaseDirectory, "AIO.Transcription.Server.dll");
            Assert.True(File.Exists(serverDll), $"Expected server assembly at {serverDll}");

            var startInfo = new ProcessStartInfo("dotnet", $"{serverDll}")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            startInfo.Environment["Hosting__Urls"] = $"http://127.0.0.1:{port}";
            startInfo.Environment["Transcription__ModelType"] = "Tiny";
            startInfo.Environment["Transcription__AutoDownloadModel"] = "true";
            startInfo.Environment["Transcription__TargetSampleRate"] = "16000";
            startInfo.Environment["Transcription__BoundaryDetectionEnabled"] = "true";
            startInfo.Environment["Transcription__BoundaryDetectionRmsThreshold"] = "0.015";
            startInfo.Environment["Transcription__BoundarySilenceMilliseconds"] = "700";
            startInfo.Environment["Transcription__MinimumSpeechMilliseconds"] = "250";
            startInfo.Environment["Transcription__MinimumWindowMilliseconds"] = "3500";
            startInfo.Environment["Transcription__MaximumSegmentMilliseconds"] = "15000";

            var process = new Process { StartInfo = startInfo };
            var started = process.Start();
            Assert.True(started, "Failed to start transcription server process for websocket integration test.");

            _ = Task.Run(async () =>
            {
                while (!process.HasExited)
                {
                    _ = await process.StandardOutput.ReadLineAsync();
                }
            }, cancellationToken);
            _ = Task.Run(async () =>
            {
                while (!process.HasExited)
                {
                    _ = await process.StandardError.ReadLineAsync();
                }
            }, cancellationToken);

            return Task.FromResult(new TestServerHost(process, port));
        }

        public async Task WaitForReadyAsync(HttpClient client, CancellationToken cancellationToken)
        {
            var healthUrl = $"http://127.0.0.1:{port}/healthz";
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var response = await client.GetAsync(healthUrl, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch
                {
                }

                await Task.Delay(200, cancellationToken);
            }

            throw new TimeoutException($"Timed out waiting for server readiness on {healthUrl}.");
        }

        public ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }

            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
