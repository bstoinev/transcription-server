# Repository Guidance

## Project Shape

- This repository is the ASP.NET Core live transcription server.
- The paired desktop client is usually checked out at `W:\github-repos\interview-assistant`.
- The WebSocket contract is documented in `docs/live-transcription-contract.md`; update it when protocol behavior changes.

## Codex Instance Boundary

- A Codex instance launched from this repo owns server-side edits only.
- Do not edit files in `W:\github-repos\interview-assistant` from this instance unless the user explicitly says this instance owns both repos for the task.
- Reading the client repo for context is allowed.
- If a requested fix belongs in the client repo, stop and provide a paste-ready prompt for the Codex instance running in `W:\github-repos\interview-assistant`.

## WebSocket Protocol

- Endpoint: `WS /ws/transcribe`.
- Keep `partial-transcript` provisional and replaceable by `sessionId` + `utteranceId`.
- Keep `final-transcript` authoritative for the utterance.
- Do not reintroduce legacy `transcript` or `isFinal` compatibility events.
- On client close frames, complete the WebSocket close handshake from both `Open` and `CloseReceived` states.

## Transcript Context

- The server owns utterance detection and should keep one stable `utteranceId` for all partials in an active utterance.
- A new `utteranceId` means a new utterance, not a new transport chunk.
- The client accumulator should replace partials for the same utterance and should not render every incoming chunk on a new line.

## Validation

- Run `dotnet test --no-restore` from this repo after server changes.
- When changing transcript display behavior, also run the paired client tests:
  `dotnet test W:\github-repos\interview-assistant\tests\InterviewAssistant.Core.Tests\InterviewAssistant.Core.Tests.csproj --no-restore`.
