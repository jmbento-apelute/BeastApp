internal sealed record AppSettings(
    string OpenAiApiKey,
    string OpenAiModel,
    string OpenAiTtsModel,
    string OpenAiTranscriptionModel,
    double OpenAiTtsSpeed,
    string VitureSdkRoot,
    string VitureMicDevice,
    string WebcamMicDevice,
    string VitureSpeakerDevice,
    string WebcamSpeakerDevice,
    int CameraIndex,
    bool VoiceCommandEnabled,
    string VoiceCommandPhrase,
    TimeSpan VoiceCommandClipDuration,
    TimeSpan VoiceCommandSilenceDuration,
    TimeSpan VoiceCommandCooldown,
    double VoiceCommandMinRms,
    bool VoiceCommandDebug,
    bool SaveCaptures,
    string CaptureSaveDirectory,
    TimeSpan VideoCaptureDuration,
    string VideoCaptureDirectory,
    string LiveStreamUrl,
    double LiveStreamFramesPerSecond,
    TimeSpan CaptureTimeout)
{
    public static AppSettings Load(string[] args)
    {
        var cameraIndex = GetInt("CAMERA_INDEX", 0);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--camera" or "-c" && int.TryParse(args[i + 1], out var parsedIndex))
            {
                cameraIndex = parsedIndex;
            }
        }

        return new AppSettings(
            Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty,
            Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-5.5",
            Environment.GetEnvironmentVariable("OPENAI_TTS_MODEL") ?? "gpt-4o-mini-tts",
            Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL") ?? "gpt-4o-mini-transcribe",
            GetDouble("OPENAI_TTS_SPEED", 1.25, 0.25, 4.0),
            Environment.GetEnvironmentVariable("VITURE_SDK_ROOT")
                ?? @"C:\Users\jmben\Downloads\VITURE_XR_Glasses_SDK_for_Windows_x86_64",
            Environment.GetEnvironmentVariable("VITURE_MIC_DEVICE") ?? string.Empty,
            Environment.GetEnvironmentVariable("WEBCAM_MIC_DEVICE") ?? string.Empty,
            Environment.GetEnvironmentVariable("VITURE_SPEAKER_DEVICE") ?? string.Empty,
            Environment.GetEnvironmentVariable("WEBCAM_SPEAKER_DEVICE") ?? string.Empty,
            cameraIndex,
            GetBool("VOICE_COMMAND_ENABLED", true),
            Environment.GetEnvironmentVariable("VOICE_COMMAND_PHRASE") ?? "gafas captura",
            TimeSpan.FromSeconds(GetDouble("VOICE_COMMAND_CLIP_SECONDS", 10.0, 1.0, 30.0)),
            TimeSpan.FromSeconds(GetDouble("VOICE_COMMAND_SILENCE_SECONDS", 0.9, 0.2, 5.0)),
            TimeSpan.FromSeconds(GetDouble("VOICE_COMMAND_COOLDOWN_SECONDS", 2.0, 0.0, 10.0)),
            GetDouble("VOICE_COMMAND_MIN_RMS", 0.004, 0.0, 1.0),
            GetBool("VOICE_COMMAND_DEBUG", false),
            GetBool("SAVE_CAPTURES", false),
            Environment.GetEnvironmentVariable("CAPTURE_SAVE_DIR") ?? Path.Combine(AppContext.BaseDirectory, "captures"),
            TimeSpan.FromSeconds(GetDouble("VIDEO_CAPTURE_SECONDS", 5.0, 1.0, 60.0)),
            Environment.GetEnvironmentVariable("VIDEO_CAPTURE_DIR")
                ?? Path.Combine(Path.GetTempPath(), "BeastApp", "videos"),
            Environment.GetEnvironmentVariable("LIVE_STREAM_URL") ?? "http://localhost:5050/",
            GetDouble("LIVE_STREAM_FPS", 30.0, 1.0, 30.0),
            TimeSpan.FromSeconds(GetInt("CAPTURE_TIMEOUT_SECONDS", 5)));
    }

    private static int GetInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
    }

    private static bool GetBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static double GetDouble(string name, double fallback, double min, double max)
    {
        return double.TryParse(
            Environment.GetEnvironmentVariable(name),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }
}
