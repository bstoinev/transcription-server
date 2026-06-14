# AIO.Transcription.Server Protocol

## WebSocket endpoint

`/ws/transcribe`

## Message types

### Client -> server
- `start-session`
- `audio-chunk`
- `simulate-text`
- `end-session`

### Server -> client
- `server-ready`
- `session-started`
- `audio-ack`
- `transcript`
- `session-ended`
- `error`

## Audio expectations

Current default client path sends:
- encoding: `f32le`
- sampleRate: `48000`
- channels: `2`

Server-side transcription windowing converts those chunks to mono 16kHz PCM WAV before whisper transcription.
