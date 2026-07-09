# BeastApp Design Document

## 1. Overview

BeastApp is a Windows console prototype for a voice-driven smart-glasses assistant. It captures an image from Viture Beast glasses or a fallback webcam, sends the image to OpenAI for scene understanding, speaks the response aloud, and accepts follow-up voice questions about the latest captured scene.

The app is intentionally local-first for device access:

- Camera and microphone input are captured on the Windows machine.
- OpenAI API calls are used for vision analysis, speech transcription, question answering, and text-to-speech.
- Captured images are kept in memory by default and are not written to disk unless explicitly enabled.

## 2. Goals

- Let the user capture a scene hands-free with the command `Gafas, captura`.
- Support both Viture Beast camera capture and ordinary webcam capture.
- Fall back from Viture capture to webcam capture when Viture capture fails.
- Speak short Spanish scene descriptions.
- Allow voice questions about the most recent captured image.
- Route audio output to Viture speakers when available.
- Avoid storing private image data locally unless diagnostic capture saving is explicitly enabled.

## 3. Non-Goals

- This is not a production installer or packaged consumer app yet.
- It does not provide a graphical UI.
- It does not include user account management.
- It does not include a backend service for hiding a provider API key.
- It does not persist conversation history or image history across app restarts.
- It does not implement fully streaming speech-to-speech interaction.

## 4. Runtime Architecture

The application is split into focused source files:

- `Program.cs`: Main orchestration loop, active source switching, capture workflow, question workflow, and anti-echo suppression.
- `Settings.cs`: Environment and command-line configuration.
- `Models.cs`: Shared records and interfaces.
- `CaptureSources.cs`: Webcam and Viture image capture implementations.
- `OpenAiServices.cs`: OpenAI vision, question answering, speech synthesis, and transcription clients.
- `VoiceCommands.cs`: Microphone selection, audio recording, command transcription, and command matching.
- `AudioServices.cs`: Audio output selection, MP3 playback, and local cue sounds.
- `VitureNative.cs`: Viture SDK P/Invoke bindings, USB device detection, and native DLL loading.
- `GlobalUsings.cs`: Common imports.

High-level flow:

```text
keyboard / voice
      |
      v
Program.cs
      |
      +--> capture source: Viture or webcam
      |
      +--> OpenAI vision analysis
      |
      +--> scene context stored in memory
      |
      +--> OpenAI TTS
      |
      +--> Windows audio output / Viture speakers
```

Voice question flow:

```text
microphone
   |
   v
short WAV recording
   |
   v
OpenAI transcription
   |
   +--> "gafas captura" -> capture workflow
   |
   +--> other phrase -> question workflow
                         |
                         v
                  OpenAI response with latest image context
                         |
                         v
                       TTS
```

## 5. Capture Sources

### Viture Beast

`VitureBeastCaptureSource` uses the Viture Windows SDK through native P/Invoke calls.

Responsibilities:

- Add the SDK `x86_64` folder to the native DLL search path.
- Detect a connected Viture USB device.
- Resolve camera VID/PID through the Viture SDK.
- Start the camera provider.
- Wait for an MJPEG frame callback.
- Return JPEG bytes to the caller.

Viture capture is usually fast because the SDK returns an MJPEG frame directly.

### Webcam

`WebcamCaptureSource` uses OpenCvSharp `VideoCapture`.

Important behavior:

- The webcam is kept open after first preparation.
- `PrepareAsync` warms up the camera by opening it and reading a first frame.
- When switching to webcam with `S`, preparation starts in the background.
- The app prints and speaks:

```text
Iniciando webcam...
Webcam lista.
```

This avoids paying webcam initialization cost on every capture. The first capture is fastest if the user waits for `Webcam lista`.

## 6. Voice Interaction

`VoiceCommandListener` runs in the background when `OPENAI_API_KEY` is configured.

The listener:

- Selects the microphone based on the active source.
- Records short WAV clips.
- Plays a short cue when voice is detected.
- Sends audio to OpenAI transcription.
- Routes exact capture commands to the capture workflow.
- Routes other non-empty phrases to question answering.

The capture command is intentionally strict:

```text
gafas captura
```

This avoids accidental captures from ordinary speech.

## 7. Scene Context

After every successful capture, the app stores a `SceneContext` in memory:

- source name
- scene description
- JPEG bytes
- capture timestamp

The latest scene context is used for follow-up questions. This means the user can ask about objects or details that were not mentioned in the initial short description, because the image itself is sent again with the question.

Example:

```text
Gafas, captura
¿Hay una taza?
¿De qué color es el cable?
¿Qué pone en la pantalla?
```

Only the latest scene is retained. A new capture replaces the previous one.

## 8. OpenAI Services

OpenAI calls are isolated in `OpenAiServices.cs`.

### Scene Analysis

`OpenAiSceneAnalyzer` sends the captured image to the Responses API and requests a concise Spanish description.

The prompt avoids unnecessary safety warnings:

- Describe main objects and relevant context.
- Do not warn about ordinary clutter, cables, furniture, or everyday objects.
- Warn only for clear and immediate danger.

### Question Answering

`OpenAiQuestionAnswerer` receives:

- the transcribed user phrase
- the latest scene description
- the latest captured JPEG, when available

It answers only if the phrase is a question or request for information. Otherwise it returns `NO_QUESTION`, which the app ignores.

### Speech Synthesis

`OpenAiSpeechSynthesizer` creates MP3 audio using the configured TTS model.

The app uses MP3 rather than WAV because it avoids playback issues previously seen with WAV stream parsing.

### Transcription

`OpenAiTranscriptionService` sends microphone WAV clips to OpenAI transcription and returns the recognized text.

## 9. Audio Output

`AudioOutputSelector` uses Windows audio endpoint enumeration.

When the active source is Viture, the app tries to find speakers with names containing:

- `viture`
- `beast`
- `glasses`
- `xr`

If no matching output is found, playback falls back to the default Windows output.

The app also plays local cues:

- voice detected cue
- capture complete camera-like cue

## 10. Anti-Echo Suppression

Because the app speaks through speakers that may be picked up by the microphone, `Program.cs` maintains a voice-input suppression window.

When the app is about to speak:

- voice input is suppressed
- any recording overlapping app-generated audio is discarded
- after playback, the listener waits briefly before accepting input again

This prevents the app from hearing itself and accidentally triggering `Gafas, captura`.

## 11. Privacy And Storage

Default behavior:

- Captured images are not saved to disk.
- The latest image is kept only in process memory.
- Closing the app clears the stored image.

Optional diagnostic saving:

```powershell
$env:SAVE_CAPTURES="true"
```

Default save folder:

```text
bin\Debug\net10.0\captures
```

Custom folder:

```powershell
$env:CAPTURE_SAVE_DIR="C:\Users\jmben\Pictures\BeastCaptures"
```

The repository ignores:

- `bin/`
- `obj/`
- `captures/`

## 12. Configuration

Configuration is read from environment variables and command-line arguments.

Important variables:

| Variable | Default | Purpose |
| --- | --- | --- |
| `OPENAI_API_KEY` | empty | Required for OpenAI calls |
| `OPENAI_MODEL` | `gpt-5.5` | Vision and question-answering model |
| `OPENAI_TTS_MODEL` | `gpt-4o-mini-tts` | Speech synthesis model |
| `OPENAI_TRANSCRIPTION_MODEL` | `gpt-4o-mini-transcribe` | Voice transcription model |
| `OPENAI_TTS_SPEED` | `1.25` | TTS speed |
| `VITURE_SDK_ROOT` | local Downloads SDK path | Viture SDK folder |
| `CAMERA_INDEX` | `0` | Webcam index |
| `VOICE_COMMAND_ENABLED` | `true` | Enable background listener |
| `VOICE_COMMAND_PHRASE` | `gafas captura` | Capture command |
| `VOICE_COMMAND_CLIP_SECONDS` | `10.0` | Max voice clip duration |
| `VOICE_COMMAND_SILENCE_SECONDS` | `0.9` | Silence needed to end a clip |
| `VOICE_COMMAND_COOLDOWN_SECONDS` | `2.0` | Delay after command handling |
| `VOICE_COMMAND_MIN_RMS` | `0.004` | Minimum audio level |
| `VOICE_COMMAND_DEBUG` | `false` | Extra voice diagnostics |
| `VITURE_MIC_DEVICE` | empty | Force Viture mic by name or index |
| `WEBCAM_MIC_DEVICE` | empty | Force webcam mic by name or index |
| `VITURE_SPEAKER_DEVICE` | empty | Force Viture speaker by name |
| `WEBCAM_SPEAKER_DEVICE` | empty | Force webcam speaker by name |
| `SAVE_CAPTURES` | `false` | Save captured JPEGs |
| `CAPTURE_SAVE_DIR` | `captures` under output folder | Capture save location |
| `CAPTURE_TIMEOUT_SECONDS` | `5` | Camera frame timeout |

Command-line:

```powershell
dotnet run --project .\BeastApp.csproj -- --camera 1
```

## 13. Security Considerations

The OpenAI API key must never be committed to source control or embedded in the binary.

Current implementation:

- Reads `OPENAI_API_KEY` from the environment.
- Does not store the key locally.
- Does not print the key.

For a public Windows app, the recommended production approaches are:

1. Let each user provide their own key and store it with Windows Credential Manager or DPAPI.
2. Use a backend service that holds the provider key server-side and authenticates users.

The app should not ship with a developer-owned API key.

## 14. Error Handling

The app favors graceful fallback:

- If Viture capture fails, it falls back to webcam.
- If the webcam is not ready, it reports a warm-up failure.
- If audio cues fail, core capture and analysis continue.
- If voice listener errors, it reports the error and retries after a delay.
- If OpenAI key is missing, capture can still be tested but OpenAI operations fail with explicit messages.

## 15. Known Limitations

- Voice input uses repeated short recordings rather than low-latency streaming.
- The console UI can interleave background listener messages with prompts.
- Viture SDK support is Windows-only.
- Webcam performance depends on camera driver warm-up behavior.
- Questions use only the most recent captured image, not a history.
- There is no packaging or installer flow yet.
- There are no automated unit tests yet.

## 16. Future Improvements

Potential next steps:

- Add a Windows UI for source selection, status, and configuration.
- Store user API keys securely with Windows Credential Manager.
- Add a Realtime API mode for lower-latency voice interaction.
- Add structured logging with privacy-safe redaction.
- Add tests for command matching, settings parsing, and prompt routing.
- Add a packaged release process.
- Add optional local capture history with explicit user consent.
- Add a backend mode for non-technical users.

## 17. Build And Run

Build:

```powershell
dotnet build BeastApp.csproj
```

Run:

```powershell
$env:OPENAI_API_KEY="your_key_here"
dotnet run --project BeastApp.csproj
```

Primary controls:

```text
C = capture
S = switch source
Q = quit
```

Voice command:

```text
Gafas, captura
```
