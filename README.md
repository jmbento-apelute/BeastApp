# BeastApp

BeastApp es un prototipo de asistente para Windows orientado a las gafas Viture Beast. Puede capturar imágenes o vídeo, analizar escenas con OpenAI, responder preguntas por voz y mostrar un stream local en el navegador.

## Funciones principales

- Captura desde Viture Beast o webcam, con fallback automático a la webcam.
- Descripción breve en español de la escena capturada.
- Comando de voz `Gafas, captura` y preguntas sobre la última imagen.
- Síntesis de voz y selección automática del dispositivo de audio.
- Grabación de clips de vídeo.
- Streaming local con efectos y captura mediante gestos.
- Guardado opcional de imágenes; por defecto permanecen únicamente en memoria.

## Requisitos

- Windows y .NET 10 SDK.
- SDK de Viture para usar las gafas (la webcam puede utilizarse sin él en tiempo de ejecución).
- Una clave de OpenAI para análisis, transcripción y síntesis de voz.

## Configuración rápida

En PowerShell:

```powershell
$env:OPENAI_API_KEY="your_key_here"
dotnet build BeastApp.csproj
dotnet run --project BeastApp.csproj
```

También se puede seleccionar otra webcam:

```powershell
dotnet run --project BeastApp.csproj -- --camera 1
```

## Controles

| Tecla | Acción |
| --- | --- |
| `C` | Capturar y analizar una imagen |
| `V` | Grabar un clip de vídeo |
| `L` | Iniciar o detener el stream local |
| `E` | Cambiar el efecto del stream |
| `S` | Alternar entre Viture y webcam |
| `Q` | Salir |

Cuando el stream está activo, la aplicación muestra la URL local que debe abrirse en Chrome. Las capturas iniciadas con `C`, con `Gafas, captura` o mediante el gesto de pinza reutilizan el último frame disponible del stream y siguen el mismo workflow: señal de captura, guardado opcional, análisis, actualización del contexto y respuesta por voz. No abren la cámara una segunda vez.

Si el stream acaba de iniciarse y aún no ha producido ningún frame, la captura se ignora con un mensaje informativo. Hay que detener el stream antes de cambiar de fuente o grabar vídeo.

Si `Gafas, captura` se reconoce mientras otra captura, pregunta, grabación o cambio del live está en curso, se conserva una solicitud pendiente y se ejecuta automáticamente al terminar la interacción actual. Durante la reproducción de audio el micrófono continúa temporalmente suprimido para evitar el eco y los disparos provocados por la propia aplicación.

La palabra de activación admite pequeñas pérdidas de transcripción, por ejemplo `gas captura`, pero la palabra de acción `captura` debe reconocerse exactamente. Esto permite recuperar inicios de frase cortados sin aceptar cualquier frase parecida como una orden.

## Variables de entorno principales

| Variable | Valor predeterminado | Uso |
| --- | --- | --- |
| `OPENAI_API_KEY` | vacío | Habilita las llamadas a OpenAI |
| `OPENAI_MODEL` | `gpt-5.5` | Análisis visual y respuestas |
| `OPENAI_TTS_MODEL` | `gpt-4o-mini-tts` | Síntesis de voz |
| `OPENAI_TRANSCRIPTION_MODEL` | `gpt-4o-mini-transcribe` | Transcripción |
| `CAMERA_INDEX` | `0` | Índice de webcam |
| `VOICE_COMMAND_ENABLED` | `true` | Activa el listener de voz |
| `VOICE_COMMAND_PHRASE` | `gafas captura` | Frase de captura |
| `SAVE_CAPTURES` | `false` | Guarda los JPEG capturados |
| `CAPTURE_SAVE_DIR` | `captures` en la salida | Carpeta de imágenes |
| `CAPTURE_TIMEOUT_SECONDS` | `5` | Timeout del primer frame |

`Settings.cs` contiene el resto de opciones de audio, voz, vídeo y streaming.

## Estructura del código

- `Program.cs`: punto de entrada; carga la configuración y ejecuta la aplicación.
- `BeastApplication.cs`: ciclo de vida, comandos y coordinación de workflows.
- `ApplicationState.cs`: estado mutable sincronizado de fuente, escena, voz y efectos.
- `Settings.cs`: configuración por argumentos y variables de entorno.
- `Models.cs`: contratos y modelos compartidos.
- `CaptureSources.cs`: fuentes Viture y webcam.
- `CaptureWorkflows.cs`: almacenamiento, nombres de archivo y fallback de captura.
- `LiveStreamingServer.cs`: servidor local y comandos del navegador.
- `LiveVideoEffects.cs`: efectos aplicados al stream.
- `OpenAiServices.cs`: análisis, preguntas, TTS y transcripción.
- `VoiceCommands.cs`: captura de micrófono y detección de comandos.
- `AudioServices.cs`: reproducción, señales y selección de salida.
- `VitureNative.cs`: interoperabilidad con el SDK nativo.

`Program.cs` se mantiene deliberadamente mínimo. La coordinación reside en `BeastApplication`, mientras que el acceso a dispositivos y servicios externos permanece aislado en componentes específicos.

## Privacidad y seguridad

- La clave se lee desde `OPENAI_API_KEY`; no debe incluirse en el repositorio.
- Las imágenes no se escriben en disco salvo que `SAVE_CAPTURES=true`.
- Solo se conserva en memoria el contexto de la última escena.
- Los vídeos se guardan explícitamente al pulsar `V`.
- El stream es local y no persiste sus frames.

Las carpetas `bin/`, `obj/` y `captures/` están excluidas del repositorio.

## Limitaciones actuales

- Es un prototipo de consola para Windows, no una aplicación empaquetada.
- La interacción por voz usa clips cortos, no audio bidireccional en tiempo real.
- El contexto contiene solamente la captura más reciente.
- Todavía no hay pruebas automatizadas.
