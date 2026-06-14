<div align="center">

# AIO.Transcription.Server

**A clean, reusable real-time transcription backend for AI Orchestra apps**

*Stream audio in. Get transcript events out.*

</div>

## What it is

AIO.Transcription.Server is a dedicated transcription service intended to run on a stronger machine than the capture client.

It is deliberately app-agnostic.

## Intended uses

- live interview assistance
- meeting tools
- voice-driven operator panels
- other internal AI Orchestra applications

## Planned responsibilities

- accept audio streams over WebSocket
- buffer and validate audio chunks
- run speech-to-text
- emit partial/final transcript events
- stay independent from any specific UI or product workflow

## Non-goals

- not tied to InterviewAssistant
- not the interview-guidance engine
- not the UI client

## Position in the system

- **Client app** captures and streams audio
- **AIO.Transcription.Server** transcribes it
- **Hydra or another consumer** interprets the text

## Status

Repository scaffold created. Implementation intentionally not started yet.
