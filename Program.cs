using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenCvSharp;

var settings = AppSettings.Load(args);

Console.WriteLine("Viture Beast Windows Assistant Prototype");
Console.WriteLine("Press C to capture, S to switch source, Q to quit.");
Console.WriteLine("Say \"Gafas, captura\" to capture hands-free.");
Console.WriteLine();

if (string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("OPENAI_API_KEY is not set. Capturing can be tested, but OpenAI calls will fail.");
    Console.ResetColor();
    Console.WriteLine();
}

using var httpClient = new HttpClient();
using var sceneAnalyzer = new OpenAiSceneAnalyzer(httpClient, settings);
using var questionAnswerer = new OpenAiQuestionAnswerer(httpClient, settings);
using var speechSynthesizer = new OpenAiSpeechSynthesizer(httpClient, settings);
using var transcriptionService = new OpenAiTranscriptionService(httpClient, settings);
using var audioPlayback = new AudioPlaybackService();
using var captureGate = new SemaphoreSlim(1, 1);
var sourceGate = new object();
var sceneContextGate = new object();
var voiceInputGate = new object();
SceneContext? lastSceneContext = null;
DateTimeOffset voiceInputSuppressedUntil = DateTimeOffset.MinValue;

IImageCaptureSource vitureSource = new VitureBeastCaptureSource(settings);
using var webcamSource = new WebcamCaptureSource(settings.CameraIndex);
IImageCaptureSource activeSource = vitureSource;
using var voiceCommandListener = new VoiceCommandListener(
    settings,
    transcriptionService,
    audioPlayback,
    () =>
    {
        lock (sourceGate)
        {
            return activeSource.Name;
        }
    },
    () => RunCaptureWorkflowAsync("voice"),
    transcript => RunQuestionWorkflowAsync(transcript),
    GetVoiceInputSuppressedUntil);

if (settings.VoiceCommandEnabled && !string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
{
    voiceCommandListener.Start();
}
else if (settings.VoiceCommandEnabled)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("Voice command listening is disabled until OPENAI_API_KEY is set.");
    Console.ResetColor();
    Console.WriteLine();
}

while (true)
{
    IImageCaptureSource promptSource;
    lock (sourceGate)
    {
        promptSource = activeSource;
    }

    Console.Write($"[{promptSource.Name}]> ");
    var key = ReadCommandKey();
    Console.WriteLine(key);

    if (key == ConsoleKey.Q)
    {
        break;
    }

    if (key == ConsoleKey.S)
    {
        IImageCaptureSource selectedSource;
        lock (sourceGate)
        {
            activeSource = ReferenceEquals(activeSource, vitureSource) ? webcamSource : vitureSource;
            selectedSource = activeSource;
            Console.WriteLine($"Active source: {selectedSource.Name}");
        }

        StartSourceWarmup(selectedSource);

        continue;
    }

    if (key != ConsoleKey.C)
    {
        continue;
    }

    await RunCaptureWorkflowAsync("keyboard");
}

void StartSourceWarmup(IImageCaptureSource source)
{
    if (ReferenceEquals(source, webcamSource))
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Iniciando webcam...");
        Console.ResetColor();
        _ = SpeakStatusAsync("Iniciando webcam.", source.Name);
    }

    _ = Task.Run(async () =>
    {
        try
        {
            await source.PrepareAsync(settings.CaptureTimeout, CancellationToken.None);
            if (ReferenceEquals(source, webcamSource))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Webcam lista.");
                Console.ResetColor();
                await SpeakStatusAsync("Webcam lista.", source.Name);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Source warm-up failed: {ex.Message}");
            Console.ResetColor();
        }
    });
}

async Task SpeakStatusAsync(string text, string sourceName)
{
    if (string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
    {
        return;
    }

    try
    {
        SuppressVoiceInput(TimeSpan.FromSeconds(30));
        var speechAudio = await speechSynthesizer.CreateSpeechAsync(text, CancellationToken.None);
        var speechOutput = AudioOutputSelector.SelectDevice(sourceName, settings);
        audioPlayback.PlayMp3(speechAudio, speechOutput);
    }
    catch (Exception ex)
    {
        if (settings.VoiceCommandDebug)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Status speech failed: {ex.Message}");
            Console.ResetColor();
        }
    }
    finally
    {
        SuppressVoiceInput(TimeSpan.FromSeconds(1));
    }
}

void SuppressVoiceInput(TimeSpan duration)
{
    lock (voiceInputGate)
    {
        voiceInputSuppressedUntil = DateTimeOffset.UtcNow + duration;
    }
}

DateTimeOffset GetVoiceInputSuppressedUntil()
{
    lock (voiceInputGate)
    {
        return voiceInputSuppressedUntil;
    }
}

async Task RunCaptureWorkflowAsync(string trigger)
{
    if (!await captureGate.WaitAsync(0))
    {
        Console.WriteLine("Capture is already in progress.");
        return;
    }

    try
    {
        IImageCaptureSource requestedSource;
        lock (sourceGate)
        {
            requestedSource = activeSource;
        }

        Console.WriteLine(trigger == "voice" ? "Voice command detected. Capturing image..." : "Capturing image...");
        var image = await CaptureWithFallbackAsync(requestedSource, vitureSource, webcamSource, settings.CaptureTimeout);
        lock (sourceGate)
        {
            activeSource = image.Source;
        }

        audioPlayback.PlayCaptureCompleteCue();
        SaveCaptureIfEnabled(image);

        Console.WriteLine($"Analyzing scene from {image.Source.Name}...");
        var description = await sceneAnalyzer.DescribeSceneAsync(image.JpegBytes, CancellationToken.None);
        lock (sceneContextGate)
        {
            lastSceneContext = new SceneContext(
                image.Source.Name,
                description,
                image.JpegBytes,
                DateTimeOffset.Now);
        }

        Console.WriteLine();
        Console.WriteLine(description);
        Console.WriteLine();

        Console.WriteLine("Generating speech...");
        try
        {
            SuppressVoiceInput(TimeSpan.FromSeconds(30));
            var speechAudio = await speechSynthesizer.CreateSpeechAsync(description, CancellationToken.None);
            var speechOutput = AudioOutputSelector.SelectDevice(requestedSource.Name, settings);
            audioPlayback.PlayMp3(speechAudio, speechOutput);
        }
        finally
        {
            SuppressVoiceInput(TimeSpan.FromSeconds(1));
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
    }
    finally
    {
        captureGate.Release();
    }
}

async Task RunQuestionWorkflowAsync(string transcript)
{
    if (!await captureGate.WaitAsync(0))
    {
        Console.WriteLine("Another interaction is already in progress.");
        return;
    }

    try
    {
        Console.WriteLine($"Voice phrase heard: {transcript}");
        SceneContext? sceneContext;
        lock (sceneContextGate)
        {
            sceneContext = lastSceneContext;
        }

        var answer = await questionAnswerer.AnswerIfQuestionAsync(transcript, sceneContext, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(answer))
        {
            if (settings.VoiceCommandDebug)
            {
                Console.WriteLine("Voice phrase was not handled as a question.");
            }

            return;
        }

        Console.WriteLine();
        Console.WriteLine(answer);
        Console.WriteLine();

        Console.WriteLine("Generating speech...");
        try
        {
            SuppressVoiceInput(TimeSpan.FromSeconds(30));
            var speechAudio = await speechSynthesizer.CreateSpeechAsync(answer, CancellationToken.None);
            string activeSourceName;
            lock (sourceGate)
            {
                activeSourceName = activeSource.Name;
            }

            var speechOutput = AudioOutputSelector.SelectDevice(activeSourceName, settings);
            audioPlayback.PlayMp3(speechAudio, speechOutput);
        }
        finally
        {
            SuppressVoiceInput(TimeSpan.FromSeconds(1));
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
    }
    finally
    {
        captureGate.Release();
    }
}

static ConsoleKey ReadCommandKey()
{
    if (!Console.IsInputRedirected)
    {
        return Console.ReadKey(intercept: true).Key;
    }

    var value = Console.Read();
    if (value < 0)
    {
        return ConsoleKey.Q;
    }

    return char.ToUpperInvariant((char)value) switch
    {
        'C' => ConsoleKey.C,
        'S' => ConsoleKey.S,
        'Q' => ConsoleKey.Q,
        _ => ConsoleKey.NoName
    };
}

void SaveCaptureIfEnabled(CapturedImage image)
{
    if (!settings.SaveCaptures)
    {
        return;
    }

    try
    {
        Directory.CreateDirectory(settings.CaptureSaveDirectory);
        var sourceName = SanitizeFileName(image.Source.Name);
        var fileName = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{sourceName}.jpg";
        var path = Path.Combine(settings.CaptureSaveDirectory, fileName);
        File.WriteAllBytes(path, image.JpegBytes);
        Console.WriteLine($"Capture saved: {path}");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Capture save failed: {ex.Message}");
        Console.ResetColor();
    }
}

static string SanitizeFileName(string value)
{
    var invalidChars = Path.GetInvalidFileNameChars();
    var builder = new StringBuilder(value.Length);

    foreach (var ch in value)
    {
        builder.Append(invalidChars.Contains(ch) ? '-' : ch);
    }

    return builder.ToString().Replace(' ', '-');
}
static async Task<CapturedImage> CaptureWithFallbackAsync(
    IImageCaptureSource requestedSource,
    IImageCaptureSource vitureSource,
    IImageCaptureSource webcamSource,
    TimeSpan timeout)
{
    try
    {
        var jpeg = await requestedSource.CaptureJpegAsync(timeout, CancellationToken.None);
        return new CapturedImage(requestedSource, jpeg);
    }
    catch (Exception ex) when (ReferenceEquals(requestedSource, vitureSource))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Viture capture failed. Falling back to webcam.");
        Console.WriteLine($"Viture error: {ex.GetType().Name}: {ex.Message}");
        Console.ResetColor();

        var jpeg = await webcamSource.CaptureJpegAsync(timeout, CancellationToken.None);
        return new CapturedImage(webcamSource, jpeg);
    }
}
