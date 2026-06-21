# Live Transcription Diagnostics

Live diagnostics are off by default. Enable them under the existing `Transcription` configuration section when investigating omitted utterances.

```json
{
  "Transcription": {
    "enableLiveDiagnostics": true,
    "logAudioChunkDiagnostics": true,
    "logVadFrameDiagnostics": false,
    "logUtteranceDiagnostics": true,
    "saveDebugUtteranceWavFiles": true,
    "debugUtteranceDirectory": "debug-audio",
    "saveDroppedUtterances": true,
    "saveFinalizedUtterances": true,
    "maxDebugUtteranceFilesPerSession": 100
  }
}
```

## Settings

- `EnableLiveDiagnostics`: master switch for live diagnostic logs and debug artifact writing.
- `LogAudioChunkDiagnostics`: logs one compact entry per received chunk after normalization, including RMS, peak sample value, VAD speech-frame presence, rolling buffer duration, and current utterance state.
- `LogVadFrameDiagnostics`: logs frame-level VAD details. This is noisy and should stay off except during short targeted captures.
- `LogUtteranceDiagnostics`: logs utterance lifecycle events such as speech start, partial requests, endpoint detection, drops, final transcription, and final event emission.
- `MaxQueuedAudioBufferMs`: caps the pre-processing live audio queue. If the worker falls behind, older queued chunks are discarded and logged so the server keeps recent audio instead of stale backlog.
- `SaveDebugUtteranceWavFiles`: writes WAV files for dropped or finalized utterances when diagnostics are enabled.
- `DebugUtteranceDirectory`: root directory for debug artifacts.
- `SaveDroppedUtterances`: writes WAV/JSON artifacts for dropped utterances when debug saving is enabled.
- `SaveFinalizedUtterances`: writes WAV/JSON artifacts for finalized utterances when debug saving is enabled.
- `MaxDebugUtteranceFilesPerSession`: caps WAV files written by one live session.

Debug artifacts are written below:

```text
debug-audio/{sessionId}/{timestamp}-{utteranceId}-{status}-{reason}.wav
debug-audio/{sessionId}/{timestamp}-{utteranceId}-{status}-{reason}.json
```

The WAV contains the exact audio segment sent to Whisper, or the segment that would have been sent for a dropped utterance. The adjacent JSON includes duration, RMS, VAD thresholds, utterance timing settings, transcript text when available, and the drop or finalize reason.

## Lifecycle Logs

With `EnableLiveDiagnostics=true` and `LogUtteranceDiagnostics=true`, look for these event names:

- `SpeechStartDetected`
- `PartialTranscriptRequested`
- `PartialTranscriptReturned`
- `UtteranceEndpointDetected`
- `UtteranceDropped`
- `FinalTranscriptRequested`
- `FinalTranscriptReturned`
- `FinalTranscriptEmitted`
- `LiveDiagnosticsSummary`

When the live input queue expires stale chunks, the server logs `Dropped obsolete queued live audio`. The session summary includes `droppedQueuedAudioChunks`, `droppedQueuedAudioBytes`, and `droppedQueuedAudioDurationMs`.

`UtteranceDropped` reasons include:

- `BelowMinimumUtteranceMs`: detected speech was shorter than `MinimumUtteranceMs`.
- `NoSpeechDetected`: an utterance closed without detected speech.
- `EmptyAudio`: the final segment contained no samples.
- `EmptyWhisperResult`: the utterance was long enough and sent to Whisper, but Whisper returned blank text.
- `Other`: fallback for unexpected drop paths.

## Interpreting Common Omissions

Case A: audio chunk RMS is near zero.

=> Client or output audio capture may be wrong, muted, or too quiet.

Case B: chunks have RMS and peak values, but VAD never opens an utterance.

=> `VadEnergyThreshold` is likely too high for the captured audio.

If the no-speech warning includes `NearThreshold=true`, captured audio is close to the configured threshold. Lower `Transcription:VadEnergyThreshold` slightly and retest.

Case C: an utterance opens, then logs `UtteranceDropped reason=BelowMinimumUtteranceMs`.

=> `MinimumUtteranceMs` is too high for short phrases, or VAD is only detecting a small part of the phrase.

Case D: the finalized debug WAV contains speech, but `FinalTranscriptReturned` has empty or wrong text.

=> Whisper, model selection, language, or prompt context is the likely cause, not VAD.

Case E: `FinalTranscriptEmitted` appears in server logs, but the client does not show the phrase.

=> The server emitted the `final-transcript` event. Investigate the client websocket receive loop, protocol handling, or UI accumulator.

Case F: logs show `Dropped obsolete queued live audio`.

=> The server fell behind and discarded stale queued chunks to preserve live behavior. Reduce Whisper latency, raise `MaxQueuedAudioBufferMs` cautiously, or accept that overload may skip obsolete audio.
