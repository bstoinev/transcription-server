# Live Transcription Contract

This is the server-side websocket contract for live transcription clients.

Endpoint: `WS /ws/transcribe`

Diagnostics for omitted utterance investigations are documented in [live-transcription-diagnostics.md](live-transcription-diagnostics.md).

## Core Semantics

- Incoming audio chunks are transport units only. A 500 ms chunk cadence is allowed, but 500 ms is not the recognition window.
- The server owns the rolling audio buffer, current utterance buffer, VAD, and endpoint detection.
- `partial-transcript` events are provisional and replaceable. A client should replace the previous partial for the same `sessionId` + `utteranceId`.
- `final-transcript` events are authoritative and close the utterance.
- Only `final-transcript` text should be sent to the AI assistant or any downstream reasoning state.
- There is no legacy `transcript` compatibility event and no `isFinal` field.

## Client To Server

Start a live session:

```json
{
  "type": "start-session",
  "sessionId": "meeting-123",
  "modelType": "medium.en",
  "prompt": "Company names: Kaizen, Hydra. Domain: .NET, gRPC, Kafka.",
  "encoding": "f32le",
  "sampleRate": 48000,
  "channels": 2
}
```

`modelType` is required on `start-session` unless the server is pinned to a specific model file path.
`prompt` is optional and is appended to the server-configured technical prompt for that live session.

Send audio as JSON when multiplexing sessions:

```json
{
  "type": "audio-chunk",
  "sessionId": "meeting-123",
  "sequence": 42,
  "audioBase64": "...",
  "encoding": "f32le",
  "sampleRate": 48000,
  "channels": 2
}
```

Binary audio frames are accepted only when exactly one session is active on the websocket and the audio format has already been established by `start-session` or a prior `audio-chunk`.

End a live session:

```json
{
  "type": "end-session",
  "sessionId": "meeting-123"
}
```

## Server To Client

### `partial-transcript`

Emitted while an utterance is still active. It replaces the previous partial for the same `sessionId` + `utteranceId`.

```json
{
  "type": "partial-transcript",
  "sessionId": "meeting-123",
  "utteranceId": "meeting-123-000001",
  "sequence": 1,
  "transcriptText": "we should move the grpc retry policy",
  "modelType": "base.en",
  "receivedChunkCount": 8,
  "receivedAudioBytes": 768000,
  "sentAtUtc": "2026-06-17T12:00:00.0000000+00:00"
}
```

Fields:

- `type`: always `"partial-transcript"`
- `sessionId`: client-owned live transcription session id
- `utteranceId`: server-owned id for the current utterance
- `sequence`: monotonic partial sequence within the utterance
- `transcriptText`: provisional text for display only
- `modelType`: effective whisper.cpp model
- `receivedChunkCount`: number of received transport chunks for the session
- `receivedAudioBytes`: number of received audio bytes for the session
- `sentAtUtc`: server send timestamp

### `final-transcript`

Emitted once after VAD endpointing or max utterance duration closes an utterance.

```json
{
  "type": "final-transcript",
  "sessionId": "meeting-123",
  "utteranceId": "meeting-123-000001",
  "transcriptText": "We should move the gRPC retry policy into the shared client.",
  "modelType": "base.en",
  "receivedChunkCount": 12,
  "receivedAudioBytes": 1152000,
  "sentAtUtc": "2026-06-17T12:00:03.0000000+00:00"
}
```

Fields:

- `type`: always `"final-transcript"`
- `sessionId`: client-owned live transcription session id
- `utteranceId`: server-owned id for the closed utterance
- `transcriptText`: authoritative utterance text
- `modelType`: effective whisper.cpp model
- `receivedChunkCount`: number of received transport chunks for the session
- `receivedAudioBytes`: number of received audio bytes for the session
- `sentAtUtc`: server send timestamp

## Default Timing Configuration

- `PartialUpdateIntervalMs`: `750`
- `PartialWindowMs`: `12000`
- `MinimumUtteranceMs`: `1800`
- `EndSilenceMs`: `1000`
- `MaxUtteranceMs`: `30000`
- `PreRollMs`: `500`
- `PostRollMs`: `700`
- `Language`: `"en"`
- `VadFrameMs`: `20`
- `VadEnergyThreshold`: `0.015`
- `PromptContextCharacters`: `256`

## Diagnostic Configuration

- `EnableLiveDiagnostics`: `false`
- `LogAudioChunkDiagnostics`: `false`
- `LogVadFrameDiagnostics`: `false`
- `LogUtteranceDiagnostics`: `true`
- `SaveDebugUtteranceWavFiles`: `false`
- `DebugUtteranceDirectory`: `"debug-audio"`
- `SaveDroppedUtterances`: `true`
- `SaveFinalizedUtterances`: `true`
- `MaxDebugUtteranceFilesPerSession`: `100`

## Whisper And Prompt Context

- whisper.cpp remains the only transcription backend.
- The live pipeline uses the server-configured language, defaulting to English.
- A server-configured technical prompt may be prepended to Whisper requests.
- Partial transcripts do not update prompt history.
- Final transcripts update prompt history and may be used by downstream AI reasoning.
