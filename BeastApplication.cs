internal sealed class BeastApplication : IDisposable
{
    private readonly AppSettings settings;
    private readonly HttpClient httpClient;
    private readonly OpenAiSceneAnalyzer sceneAnalyzer;
    private readonly OpenAiQuestionAnswerer questionAnswerer;
    private readonly OpenAiSpeechSynthesizer speechSynthesizer;
    private readonly OpenAiTranscriptionService transcriptionService;
    private readonly AudioPlaybackService audioPlayback;
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly ILiveFrameSource vitureSource;
    private readonly WebcamCaptureSource webcamSource;
    private readonly LiveStreamingServer liveStreamingServer;
    private readonly VoiceCommandListener voiceCommandListener;
    private readonly CaptureFileStore captureFileStore;
    private readonly CaptureFallbackService captureFallbackService;
    private readonly ApplicationState state;
    private int pendingVoiceCapture;

    public BeastApplication(AppSettings settings)
    {
        this.settings = settings;
        httpClient = new HttpClient();
        sceneAnalyzer = new OpenAiSceneAnalyzer(httpClient, settings);
        questionAnswerer = new OpenAiQuestionAnswerer(httpClient, settings);
        speechSynthesizer = new OpenAiSpeechSynthesizer(httpClient, settings);
        transcriptionService = new OpenAiTranscriptionService(httpClient, settings);
        audioPlayback = new AudioPlaybackService();
        vitureSource = new VitureBeastCaptureSource(settings);
        webcamSource = new WebcamCaptureSource(settings.CameraIndex);
        state = new ApplicationState(vitureSource);
        captureFileStore = new CaptureFileStore(settings);
        captureFallbackService = new CaptureFallbackService(vitureSource, webcamSource, captureFileStore);
        liveStreamingServer = new LiveStreamingServer(settings);
        voiceCommandListener = new VoiceCommandListener(
            settings,
            transcriptionService,
            audioPlayback,
            () => state.ActiveSource.Name,
            () => RunCaptureWorkflowAsync("voice"),
            RunQuestionWorkflowAsync,
            GetVoiceInputSuppressedUntil);
    }

    public async Task RunAsync()
    {
        PrintWelcome();

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
            var promptSource = state.ActiveSource;

            Console.Write($"[{promptSource.Name}]> ");
            var key = ReadCommandKey();
            Console.WriteLine(key);

            if (!await ExecuteCommandKeyAsync(key, "keyboard"))
            {
                break;
            }
        }
    }

    private void PrintWelcome()
    {
        Console.WriteLine("Viture Beast Windows Assistant Prototype");
        Console.WriteLine($"Press C to capture, V to record {settings.VideoCaptureDuration.TotalSeconds:0.#} seconds of video, L for live stream, E for live effect, S to switch source, Q to quit.");
        Console.WriteLine("Say \"Gafas, captura\" to capture hands-free.");
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("OPENAI_API_KEY is not set. Capturing can be tested, but OpenAI calls will fail.");
            Console.ResetColor();
            Console.WriteLine();
        }
    }

private void StartSourceWarmup(IImageCaptureSource source)
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
    state.SuppressVoiceInput(duration);
}

DateTimeOffset GetVoiceInputSuppressedUntil()
{
    return state.VoiceInputSuppressedUntil;
}

async Task ToggleLiveStreamAsync()
{
    if (liveStreamingServer.IsRunning)
    {
        await liveStreamingServer.StopAsync();
        Console.WriteLine("Live stream stopped.");
        audioPlayback.PlayCaptureCompleteCue();
        return;
    }

    if (!await captureGate.WaitAsync(0))
    {
        Console.WriteLine("Another interaction is already in progress.");
        return;
    }

    try
    {
        var requestedSource = state.ActiveSource;

        liveStreamingServer.Start(
            requestedSource,
            GetActiveLiveEffect,
            (jpegBytes, sourceName) => RunLiveFrameCaptureWorkflowAsync(jpegBytes, sourceName, "gesture"),
            key => ExecuteCommandKeyAsync(key, "browser"));
        audioPlayback.PlayRecordingStartedCue();
        Console.WriteLine($"Live stream started from {requestedSource.Name}: {liveStreamingServer.Url}");
        Console.WriteLine($"Live effect: {LiveVideoEffects.GetDisplayName(GetActiveLiveEffect())}");
        Console.WriteLine($"Open in Chrome: {liveStreamingServer.Url}");
        Console.WriteLine("Press L again to stop live stream.");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Live stream error: {ex.Message}");
        Console.ResetColor();
        await liveStreamingServer.StopAsync();
    }
    finally
    {
        ReleaseInteraction();
    }
}

async Task<bool> ExecuteCommandKeyAsync(ConsoleKey key, string trigger)
{
    if (key == ConsoleKey.Q)
    {
        Console.WriteLine(trigger == "browser" ? "Browser command: Q. Quitting..." : "Quitting...");
        await liveStreamingServer.StopAsync();
        if (trigger == "browser")
        {
            Environment.Exit(0);
        }

        return false;
    }

    if (key == ConsoleKey.S)
    {
        if (liveStreamingServer.IsRunning)
        {
            Console.WriteLine("Stop live stream with L before switching source.");
            return true;
        }

        var selectedSource = state.ToggleSource(vitureSource, webcamSource);
        Console.WriteLine($"Active source: {selectedSource.Name}");

        StartSourceWarmup(selectedSource);
        return true;
    }

    if (key == ConsoleKey.L)
    {
        await ToggleLiveStreamAsync();
        return true;
    }

    if (key == ConsoleKey.E)
    {
        RotateLiveEffect();
        return true;
    }

    if (key == ConsoleKey.V)
    {
        await RunVideoCaptureWorkflowAsync();
        return true;
    }

    if (key == ConsoleKey.C)
    {
        await RunCaptureWorkflowAsync(trigger);
        return true;
    }

    return true;
}

void RotateLiveEffect()
{
    var nextEffect = state.RotateLiveEffect();
    Console.WriteLine($"Live effect: {LiveVideoEffects.GetDisplayName(nextEffect)}");
}

LiveVideoEffect GetActiveLiveEffect()
{
    return state.GetLiveEffect();
}

async Task RunCaptureWorkflowAsync(string trigger)
{
    if (liveStreamingServer.IsRunning)
    {
        if (!liveStreamingServer.TryGetLatestFrame(out var jpegBytes, out var sourceName))
        {
            Console.WriteLine("Live stream is starting. No frame is available yet.");
            return;
        }

        await RunLiveFrameCaptureWorkflowAsync(jpegBytes, sourceName, trigger);
        return;
    }

    if (!await captureGate.WaitAsync(0))
    {
        if (trigger == "voice")
        {
            QueueVoiceCapture();
        }
        else
        {
            Console.WriteLine("Capture is already in progress.");
        }

        return;
    }

    try
    {
        var requestedSource = state.ActiveSource;

        Console.WriteLine(trigger == "voice" ? "Voice command detected. Capturing image..." : "Capturing image...");
        var image = await captureFallbackService.CaptureImageAsync(
            requestedSource,
            settings.CaptureTimeout,
            CancellationToken.None);
        state.SelectSource((ILiveFrameSource)image.Source);

        audioPlayback.PlayCaptureCompleteCue();
        await AnalyzeAndSpeakAsync(image, requestedSource.Name);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
    }
    finally
    {
        ReleaseInteraction();
    }
}

async Task RunLiveFrameCaptureWorkflowAsync(byte[] jpegBytes, string sourceName, string trigger)
{
    if (!await captureGate.WaitAsync(0))
    {
        if (trigger == "voice")
        {
            QueueVoiceCapture();
        }
        else
        {
            Console.WriteLine($"{GetCaptureTriggerName(trigger)} capture ignored: another interaction is already in progress.");
        }

        return;
    }

    try
    {
        var requestedSource = state.ActiveSource;

        var image = new CapturedImage(requestedSource, jpegBytes);
        Console.WriteLine($"{GetCaptureTriggerName(trigger)} detected. Capturing live frame from {sourceName}...");
        audioPlayback.PlayCaptureCompleteCue();
        await AnalyzeAndSpeakAsync(image, image.Source.Name);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
    }
    finally
    {
        ReleaseInteraction();
    }
}

void QueueVoiceCapture()
{
    if (Interlocked.Exchange(ref pendingVoiceCapture, 1) == 0)
    {
        Console.WriteLine("Voice capture queued until the current interaction finishes.");
    }
}

void ReleaseInteraction()
{
    captureGate.Release();
    if (Interlocked.Exchange(ref pendingVoiceCapture, 0) == 1)
    {
        _ = Task.Run(() => RunCaptureWorkflowAsync("voice"));
    }
}

static string GetCaptureTriggerName(string trigger) => trigger switch
{
    "gesture" => "Gesture",
    "voice" => "Voice command",
    "browser" => "Browser command",
    _ => "Capture command"
};

async Task AnalyzeAndSpeakAsync(CapturedImage image, string speechSourceName)
{
    SaveCaptureIfEnabled(image);
    Console.WriteLine($"Analyzing scene from {image.Source.Name}...");

    var description = await sceneAnalyzer.DescribeSceneAsync(image.JpegBytes, CancellationToken.None);
    state.UpdateScene(new SceneContext(
        image.Source.Name,
        description,
        image.JpegBytes,
        DateTimeOffset.Now));

    Console.WriteLine();
    Console.WriteLine(description);
    Console.WriteLine();
    Console.WriteLine("Generating speech...");

    try
    {
        SuppressVoiceInput(TimeSpan.FromSeconds(30));
        var speechAudio = await speechSynthesizer.CreateSpeechAsync(description, CancellationToken.None);
        var speechOutput = AudioOutputSelector.SelectDevice(speechSourceName, settings);
        audioPlayback.PlayMp3(speechAudio, speechOutput);
    }
    finally
    {
        SuppressVoiceInput(TimeSpan.FromSeconds(1));
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
        var sceneContext = state.SceneContext;

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
            var speechOutput = AudioOutputSelector.SelectDevice(state.ActiveSource.Name, settings);
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
        ReleaseInteraction();
    }
}

async Task RunVideoCaptureWorkflowAsync()
{
    if (liveStreamingServer.IsRunning)
    {
        Console.WriteLine("Stop live stream with L before recording video.");
        return;
    }

    if (!await captureGate.WaitAsync(0))
    {
        Console.WriteLine("Another interaction is already in progress.");
        return;
    }

    try
    {
        IVideoCaptureSource requestedSource = state.ActiveSource;

        Console.WriteLine($"Recording {settings.VideoCaptureDuration.TotalSeconds:0.#} seconds of video from {requestedSource.Name}...");
        audioPlayback.PlayRecordingStartedCue();
        var video = await captureFallbackService.CaptureVideoAsync(
            requestedSource,
            settings.VideoCaptureDuration,
            settings.CaptureTimeout,
            CancellationToken.None);

        state.SelectSource((ILiveFrameSource)video.Source);

        audioPlayback.PlayCaptureCompleteCue();
        Console.WriteLine($"Video saved: {video.FilePath}");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
    }
    finally
    {
        ReleaseInteraction();
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
        'V' => ConsoleKey.V,
        'L' => ConsoleKey.L,
        'E' => ConsoleKey.E,
        'S' => ConsoleKey.S,
        'Q' => ConsoleKey.Q,
        _ => ConsoleKey.NoName
    };
}

void SaveCaptureIfEnabled(CapturedImage image)
{
    try
    {
        captureFileStore.SaveImageIfEnabled(image);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Capture save failed: {ex.Message}");
        Console.ResetColor();
    }
}

public void Dispose()
{
    voiceCommandListener.Dispose();
    liveStreamingServer.Dispose();
    webcamSource.Dispose();
    audioPlayback.Dispose();
    transcriptionService.Dispose();
    speechSynthesizer.Dispose();
    questionAnswerer.Dispose();
    sceneAnalyzer.Dispose();
    httpClient.Dispose();
    captureGate.Dispose();
}
}
