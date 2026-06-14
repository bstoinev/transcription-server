<div align="center">

# AIO.Transcription.Server

**A clean, reusable real-time transcription backend for AI Orchestra apps**

*Stream audio in. Get transcript events out.*

</div>

## Why this exists

`AIO.Transcription.Server` is the machine-side transcription service.

It is intentionally **not** tied to InterviewAssistant.
That makes it reusable across:

- interview support
- meeting tooling
- operator consoles
- other AI Orchestra voice workflows

## Current implementation

This repo now contains the first working server scaffold:

- ASP.NET Core service targeting `.NET 10`
- WebSocket endpoint for incoming audio sessions
- health endpoint
- in-memory session tracking
- honest simulation path for transcript events while real STT is still pending

## Architecture

```mermaid
flowchart LR
    A[Client app] -->|WebSocket audio stream| B[AIO.Transcription.Server]
    B -->|partial/final transcript events| A
    B -->|optional downstream text consumers| C[Hydra / other apps]
```

## Endpoints

- `GET /healthz`
- `GET /sessions`
- `WS /ws/transcribe`

## Protocol shape

### Client → server

```json
{
  "type": "start-session",
  "sessionId": "demo-1",
  "encoding": "pcm_s16le",
  "sampleRate": 16000,
  "channels": 1
}
```

```json
{
  "type": "audio-chunk",
  "sessionId": "demo-1",
  "sequence": 1,
  "audioBase64": "..."
}
```

```json
{
  "type": "simulate-text",
  "sessionId": "demo-1",
  "simulatedText": "Can you explain the tradeoff here?",
  "isFinalChunk": true
}
```

### Server → client

```json
{
  "type": "transcript",
  "sessionId": "demo-1",
  "message": "Simulated transcript event.",
  "transcriptText": "Can you explain the tradeoff here?",
  "isFinal": true
}
```

## Solution layout

```text
src/
  AIO.Transcription.Server.Contracts/
  AIO.Transcription.Server/
```

## Design notes

- STT engine wiring is not faked here.
- The current scaffold is explicit about what is implemented now.
- `simulate-text` exists only to exercise downstream consumers before real transcription is plugged in.

## Status

Transport scaffold created.
Real STT integration is the next step.
