# BeastApp

BeastApp is a prototype Windows assistant designed for Viture Beast glasses. It can capture images or video, analyze scenes with OpenAI, answer voice questions, and display a local browser stream.

## Key features

- Capture from Viture Beast or a webcam, with automatic fallback to the webcam.
- Brief scene descriptions in Spanish.
- The `Gafas, captura` voice command and questions about the latest image.
- Speech synthesis and automatic audio device selection.
- Video clip recording.
- Local streaming with effects and gesture-based capture.
- Optional image storage; captures remain only in memory by default.

## Requirements

- Windows and the .NET 10 SDK.
- The Viture SDK to use the glasses (the webcam can be used at runtime without it).
- An OpenAI API key for analysis, transcription, and speech synthesis.

## Quick start

In PowerShell:

```powershell
$env:OPENAI_API_KEY="your_key_here"
dotnet build BeastApp.csproj
dotnet run --project BeastApp.csproj
```

You can also select a different webcam:

```powershell
dotnet run --project BeastApp.csproj -- --camera 1
```

## Controls

| Key | Action |
| --- | --- |
| `C` | Capture and analyze an image |
| `V` | Record a video clip |
| `L` | Start or stop the local stream |
| `E` | Change the stream effect |
| `S` | Switch between Viture and webcam |
| `Q` | Quit |

When the stream is active, the application displays the local URL to open in Chrome. Captures initiated with `C`, with `Gafas, captura`, or through the pinch gesture reuse the latest available stream frame and follow the same workflow: capture signal, optional storage, analysis, context update, and voice response. They do not open the camera a second time.

If the stream has just started and has not produced a frame yet, the capture is ignored with an informational message. Stop the stream before switching sources or recording video.

If `Gafas, captura` is recognized while another capture, question, recording, or live-stream change is in progress, one request is kept pending and runs automatically when the current interaction finishes. During audio playback, the microphone remains temporarily suppressed to prevent echoes and triggers caused by the application itself.

The wake phrase tolerates minor transcription losses, such as `gas captura`, but the action word `captura` must be recognized exactly. This recovers truncated phrase beginnings without accepting every similar-sounding phrase as a command.

## Main environment variables

| Variable | Default value | Purpose |
| --- | --- | --- |
| `OPENAI_API_KEY` | empty | Enables OpenAI API calls |
| `OPENAI_MODEL` | `gpt-5.5` | Visual analysis and responses |
| `OPENAI_TTS_MODEL` | `gpt-4o-mini-tts` | Speech synthesis |
| `OPENAI_TRANSCRIPTION_MODEL` | `gpt-4o-mini-transcribe` | Transcription |
| `CAMERA_INDEX` | `0` | Webcam index |
| `VOICE_COMMAND_ENABLED` | `true` | Enables the voice listener |
| `VOICE_COMMAND_PHRASE` | `gafas captura` | Capture phrase |
| `SAVE_CAPTURES` | `false` | Saves captured JPEG files |
| `CAPTURE_SAVE_DIR` | `captures` in the output directory | Image directory |
| `CAPTURE_TIMEOUT_SECONDS` | `5` | Timeout for the first frame |

`Settings.cs` contains the remaining audio, voice, video, and streaming options.

## Code structure

- `Program.cs`: entry point; loads configuration and runs the application.
- `BeastApplication.cs`: application lifecycle, commands, and workflow coordination.
- `ApplicationState.cs`: synchronized mutable state for the source, scene, voice, and effects.
- `Settings.cs`: configuration through arguments and environment variables.
- `Models.cs`: shared contracts and models.
- `CaptureSources.cs`: Viture and webcam sources.
- `CaptureWorkflows.cs`: storage, file naming, and capture fallback.
- `LiveStreamingServer.cs`: local server and browser commands.
- `LiveVideoEffects.cs`: effects applied to the stream.
- `OpenAiServices.cs`: analysis, questions, TTS, and transcription.
- `VoiceCommands.cs`: microphone capture and command detection.
- `AudioServices.cs`: playback, signals, and output selection.
- `VitureNative.cs`: interoperability with the native SDK.

`Program.cs` is deliberately kept minimal. Coordination lives in `BeastApplication`, while access to devices and external services remains isolated in dedicated components.

## Privacy and security

- The API key is read from `OPENAI_API_KEY` and must not be committed to the repository.
- Images are not written to disk unless `SAVE_CAPTURES=true`.
- Only the latest scene context is retained in memory.
- Videos are explicitly saved when `V` is pressed.
- The stream is local and does not persist its frames.

The `bin/`, `obj/`, and `captures/` directories are excluded from the repository.

## Current limitations

- This is a Windows console prototype, not a packaged application.
- Voice interaction uses short clips rather than real-time bidirectional audio.
- The context contains only the most recent capture.
- There are no automated tests yet.
