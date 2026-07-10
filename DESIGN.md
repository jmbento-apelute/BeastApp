# BeastApp Design Document

## 1. Overview

BeastApp is a Windows console prototype for a smart-glasses assistant. It captures images from Viture Beast glasses or a fallback webcam, sends images to OpenAI for scene understanding, speaks Spanish responses aloud, accepts follow-up voice questions about the latest captured scene, records short video clips, and can serve a local live video stream for browser viewing and gesture interaction.

The app is local-first for device access:

- Camera and microphone input are captured on the Windows machine.
- OpenAI API calls are used for vision analysis, speech transcription, question answering, and text-to-speech.
- Captured images are kept in memory by default and are not written to disk unless explicitly enabled.
- Video clips are written only when the user presses `V`.
- Live streaming is served locally by default and does not persist frames to disk.
- Browser gestures and browser key commands are sent to the local BeastApp server.

## 2. Goals

- Capture a scene hands-free with the command `Gafas, captura`.
- Support both Viture Beast camera capture and ordinary webcam capture.
- Fall back from Viture capture to webcam capture when Viture capture fails.
- Speak short Spanish scene descriptions.
- Allow voice questions about the most recent captured image.
- Route audio output to Viture speakers when available.
- Record short temporary video clips from the active camera source.
- Serve a local MJPEG live stream from the active camera source for quick browser testing.
- Let Chrome act as a live client for effects, keyboard commands, and pinch gesture capture.
- Avoid storing private image data locally unless diagnostic capture saving is explicitly enabled.

## 3. Non-Goals

- This is not a production installer or packaged consumer app yet.
- It does not provide a graphical desktop UI.
- It does not include user account management.
- It does not include a backend service for hiding a provider API key.
- It does not persist conversation history or image history across app restarts.
- It does not implement fully streaming speech-to-speech interaction.
- It does not implement WebRTC yet; live video currently uses local HTTP MJPEG streaming.
- It does not perform gesture detection in C# yet; the first gesture implementation runs in Chrome.

## 4. Runtime Architecture

The application is split into focused source files:

- `Program.cs`: Main orchestration loop, active source switching, capture workflow, video workflow, live workflow, question workflow, and anti-echo suppression.
- `Settings.cs`: Environment and command-line configuration.
- `Models.cs`: Shared records and interfaces.
- `CaptureSources.cs`: Webcam and Viture image, video, and live frame capture implementations.
- `LiveStreamingServer.cs`: Local HTTP MJPEG live streaming server.
- `LiveVideoEffects.cs`: OpenCV-based live video effects.
- `OpenAiServices.cs`: OpenAI vision, question answering, speech synthesis, and transcription clients.
- `VoiceCommands.cs`: Microphone selection, audio recording, command transcription, and command matching.
- `AudioServices.cs`: Audio output selection, MP3 playback, and local cue sounds.
- `VitureNative.cs`: Viture SDK P/Invoke bindings, USB device detection, and native DLL loading.
- `GlobalUsings.cs`: Common imports.

Image capture flow:

```text
keyboard C / voice command
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

Video recording flow:

```text
keyboard V
   |
   v
active source: Viture or webcam
   |
   v
temporary AVI/MJPEG file
   |
   v
start/end cue sounds
```

Live streaming flow:

```text
keyboard L
   |
   v
LiveStreamingServer
   |
   +--> GET /              browser page
   +--> GET /stream.mjpeg  MJPEG live stream
   +--> GET /snapshot.jpg  single JPEG frame
   +--> GET /effect.json   current live effect
   +--> POST /gesture/capture
   +--> POST /command/key
```

Browser gesture flow:

```text
Chrome live page
   |
   +--> MediaPipe Hands detects thumb/index pinch
   |
   +--> local click sound in Chrome
   |
   +--> POST /gesture/capture
   |
   +--> BeastApp analyzes latest raw live frame
   |
   +--> OpenAI TTS response
```

## 5. Capture Sources

### Viture Beast

`VitureBeastCaptureSource` uses the Viture Windows SDK through native P/Invoke calls.

Responsibilities:

- Add the SDK `x86_64` folder to the native DLL search path.
- Detect a connected Viture USB device.
- Resolve camera VID/PID through the Viture SDK.
- Start the camera provider.
- Receive MJPEG frames through the SDK callback.
- Return JPEG bytes for still capture.
- Write short AVI/MJPEG video clips.
- Stream MJPEG frames for local live viewing.

Viture capture is usually fast because the SDK returns an MJPEG frame directly.

For live streaming, Viture frames use a latest-frame-wins buffer. If the browser cannot consume frames as quickly as the camera produces them, old queued frames are dropped and the latest frame is preferred. This avoids delayed playback and repeated-frame effects.

### Webcam

`WebcamCaptureSource` uses OpenCvSharp `VideoCapture`.

Important behavior:

- The webcam is kept open after first preparation.
- `PrepareAsync` warms up the camera by opening it and reading a first frame.
- When switching to webcam with `S`, preparation starts in the background.
- Webcam still capture, video recording, and live streaming reuse the warmed `VideoCapture` instance.
- The app prints and speaks:

```text
Iniciando webcam...
Webcam lista.
```

This avoids paying webcam initialization cost on every capture. The first capture is fastest if the user waits for `Webcam lista`.

## 6. Video Recording

Pressing `V` records a short video clip from the active source.

Default behavior:

- Duration defaults to 5 seconds.
- Files are saved as AVI/MJPEG.
- The default folder is under the system temp directory:

```text
%TEMP%\BeastApp\videos
```

The app plays a local cue when recording starts and another cue when recording finishes.

Viture video recording uses the SDK MJPEG callback. The output video is written at 30 fps. If fewer unique frames are written than expected, the last frame is repeated as needed so the saved file duration matches the requested recording duration.

## 7. Live Streaming

Pressing `L` toggles the local live streaming server.

Default URL:

```text
http://localhost:5050/
```

Endpoints:

| Endpoint | Purpose |
| --- | --- |
| `/` | Minimal browser page with the live view |
| `/stream.mjpeg` | Multipart MJPEG stream |
| `/snapshot.jpg` | One JPEG frame |
| `/effect.json` | Current live effect name |
| `/gesture/capture` | Trigger capture from latest live frame |
| `/command/key` | Execute a registered app key from Chrome |

Live streaming is intended as a quick local test path before adding a more complex WebRTC implementation. It uses Chrome or another browser as the client and does not write frames to disk.

Only one live stream client is allowed at a time. While live streaming is active, the app blocks source switching, image capture, and video recording so the camera is not opened by two workflows at once.

### Live Effects

Pressing `E` cycles the active live effect. Effects are applied only to frames sent to the browser; image capture, video recording, and OpenAI analysis use the raw camera frame.

Current effects:

- Normal
- Blanco y negro
- Baja luz
- Vision nocturna
- Termico
- Bordes
- Bordes superpuestos
- Movimiento

### Browser Commands

The Chrome live page forwards registered key commands to BeastApp:

| Key | Behavior |
| --- | --- |
| `E` | Change live effect |
| `L` | Start/stop live stream |
| `C` | Capture the latest raw live frame |
| `V` | Attempt video recording |
| `S` | Attempt source switch |
| `Q` | Stop the app |
| `G` | Toggle browser gesture detection only |

`C` from Chrome is special: while live is active it captures the latest raw live frame instead of reopening the camera.

### Gesture Capture

The Chrome live page can load MediaPipe Hands from a CDN and detect a pinch gesture between thumb and index finger. The gesture is used as a hands-free capture trigger.

Behavior:

- Press `G` in Chrome to enable or disable gestures.
- Pinch thumb and index finger to play a local click sound.
- The page sends `POST /gesture/capture` to BeastApp.
- BeastApp analyzes the latest raw live frame and speaks the result.
- The prompt tells the LLM to ignore the pinch hand gesture unless the user explicitly asks about it.

## 8. Voice Interaction

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

## 9. Scene Context

After every successful capture, the app stores a `SceneContext` in memory:

- source name
- scene description
- JPEG bytes
- capture timestamp

The latest scene context is used for follow-up questions. This means the user can ask about objects or details that were not mentioned in the initial short description, because the image itself is sent again with the question.

Only the latest scene is retained. A new capture replaces the previous one.

## 10. OpenAI Services

OpenAI calls are isolated in `OpenAiServices.cs`.

### Scene Analysis

`OpenAiSceneAnalyzer` sends the captured image to the Responses API and requests a concise Spanish description.

The prompt avoids unnecessary safety warnings:

- Describe main objects and relevant context.
- Ignore a hand making a thumb/index pinch gesture when it is only the capture trigger.
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

## 11. Audio Output

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
- video recording start cue
- video recording end cue
- live stream start/stop cues

## 12. Anti-Echo Suppression

Because the app speaks through speakers that may be picked up by the microphone, `Program.cs` maintains a voice-input suppression window.

When the app is about to speak:

- voice input is suppressed
- any recording overlapping app-generated audio is discarded
- after playback, the listener waits briefly before accepting input again

This prevents the app from hearing itself and accidentally triggering `Gafas, captura`.

## 13. Privacy And Storage

Default behavior:

- Captured images are not saved to disk.
- The latest image is kept only in process memory.
- Closing the app clears the stored image.
- Live streaming does not persist frames.
- The live server binds to `localhost` by default.

Optional diagnostic image saving:

```powershell
$env:SAVE_CAPTURES="true"
```

Default image save folder:

```text
bin\Debug\net10.0\captures
```

Custom image folder:

```powershell
$env:CAPTURE_SAVE_DIR="C:\Users\jmben\Pictures\BeastCaptures"
```

Video recording:

- Video files are saved only when the user presses `V`.
- The default video folder is under `%TEMP%`.
- Video files are not automatically deleted by the app.

Live streaming:

- The default server URL is local-only.
- Exposing the stream to the LAN should require an explicit URL change and should be protected before broader use.
- Gesture capture uses the latest raw live frame kept in process memory; it does not save the frame unless `SAVE_CAPTURES=true`.
- The Chrome gesture prototype loads MediaPipe Hands from `cdn.jsdelivr.net`.

The repository ignores:

- `bin/`
- `obj/`
- `captures/`

## 14. Configuration

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
| `VIDEO_CAPTURE_SECONDS` | `5.0` | Video clip duration for `V` |
| `VIDEO_CAPTURE_DIR` | `%TEMP%\BeastApp\videos` | Video clip output folder |
| `LIVE_STREAM_URL` | `http://localhost:5050/` | Local live server URL |
| `LIVE_STREAM_FPS` | `30.0` | Live stream target frame rate |
| `CAPTURE_TIMEOUT_SECONDS` | `5` | Camera frame timeout |

Command-line:

```powershell
dotnet run --project .\BeastApp.csproj -- --camera 1
```

## 15. Security Considerations

The OpenAI API key must never be committed to source control or embedded in the binary.

Current implementation:

- Reads `OPENAI_API_KEY` from the environment.
- Does not store the key locally.
- Does not print the key.

For a public Windows app, the recommended production approaches are:

1. Let each user provide their own key and store it with Windows Credential Manager or DPAPI.
2. Use a backend service that holds the provider key server-side and authenticates users.

The app should not ship with a developer-owned API key.

The live streaming server exposes camera frames. It is intentionally local-only by default. If the URL is changed to listen on a LAN address, the app should add authentication or another explicit access control before being used outside a trusted local test environment.

Browser commands are also local-only by default. If the server is exposed beyond localhost, command endpoints such as `/command/key` and `/gesture/capture` must be protected because they can trigger capture, speech, live stop/start, and app shutdown.

## 16. Error Handling

The app favors graceful fallback:

- If Viture image capture fails, it falls back to webcam image capture.
- If Viture video recording fails, it falls back to webcam video recording.
- If the webcam is not ready, it reports a warm-up failure.
- If audio cues fail, core capture and analysis continue.
- If voice listener errors, it reports the error and retries after a delay.
- If OpenAI key is missing, capture can still be tested but OpenAI operations fail with explicit messages.
- If live streaming is active, conflicting camera workflows are rejected until the user stops live mode with `L`.
- If gesture capture fires while another interaction is running, the gesture capture is ignored.
- If Chrome sends `C` during live mode, BeastApp captures the latest live frame instead of reopening the active camera.

## 17. Known Limitations

- Voice input uses repeated short recordings rather than low-latency streaming.
- The console UI can interleave background listener messages with prompts.
- Viture SDK support is Windows-only.
- Webcam performance depends on camera driver warm-up behavior.
- Questions use only the most recent captured image, not a history.
- Live streaming uses MJPEG over HTTP rather than WebRTC.
- Live streaming is local-first and currently supports one stream client at a time.
- Gesture detection currently runs in Chrome and requires network access to load MediaPipe from CDN.
- Browser command endpoints are intended for local trusted use only.
- There is no packaging or installer flow yet.
- There are no automated unit tests yet.

## 18. Future Improvements

Potential next steps:

- Add a Windows UI for source selection, status, and configuration.
- Store user API keys securely with Windows Credential Manager.
- Add a Realtime API mode for lower-latency voice interaction.
- Add WebRTC live streaming for lower latency, audio support, and remote browser clients.
- Add authentication for non-local live streaming.
- Bundle MediaPipe assets locally or move gesture detection into the app for offline operation.
- Add more gesture commands, such as double pinch, pinch hold, swipe, and open palm cancel.
- Add structured logging with privacy-safe redaction.
- Add tests for command matching, settings parsing, and prompt routing.
- Add a packaged release process.
- Add optional local capture history with explicit user consent.
- Add a backend mode for non-technical users.

## 19. Build And Run

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
C = capture image
V = record video clip
L = start/stop local live stream
E = change live effect
S = switch source
Q = quit
```

Chrome live page controls:

```text
G = enable/disable browser gesture detection
Pinch thumb + index = capture latest live frame
E/L/C/V/S/Q = send registered command to BeastApp
```

Voice command:

```text
Gafas, captura
```
