internal sealed class VoiceCommandListener(
    AppSettings settings,
    OpenAiTranscriptionService transcriptionService,
    AudioPlaybackService audioPlayback,
    Func<string> getActiveSourceName,
    Func<Task> onCaptureRequested,
    Func<string, Task> onQuestionAsked,
    Func<DateTimeOffset> getVoiceInputSuppressedUntil) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private Task? listenTask;
    private string lastStatus = string.Empty;

    public void Start()
    {
        listenTask ??= Task.Run(() => ListenAsync(cancellation.Token));
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try
        {
            listenTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        cancellation.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (IsVoiceInputSuppressed())
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var activeSourceName = getActiveSourceName();
                var microphone = MicrophoneSelector.SelectDevice(activeSourceName, settings);
                if (microphone is null)
                {
                    ReportStatus("No microphone input devices were found.");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                ReportStatus($"Voice command listening on microphone: {microphone.Value.Name}");
                var voiceCueOutput = AudioOutputSelector.SelectDevice(activeSourceName, settings);
                var recordingStartedAt = DateTimeOffset.UtcNow;
                var clip = await MicrophoneRecorder.RecordWavAsync(
                    microphone.Value.DeviceNumber,
                    settings.VoiceCommandClipDuration,
                    settings.VoiceCommandMinRms,
                    settings.VoiceCommandSilenceDuration,
                    () => audioPlayback.PlayVoiceDetectedCue(voiceCueOutput),
                    cancellationToken).ConfigureAwait(false);

                if (getVoiceInputSuppressedUntil() > recordingStartedAt)
                {
                    if (settings.VoiceCommandDebug)
                    {
                        Console.WriteLine("Voice input ignored because application audio was playing.");
                    }

                    continue;
                }

                if (clip.Rms < settings.VoiceCommandMinRms)
                {
                    continue;
                }

                var transcript = await transcriptionService.TranscribeAsync(clip.WavBytes, cancellationToken).ConfigureAwait(false);
                if (!VoiceCommandMatcher.IsMatch(transcript, settings.VoiceCommandPhrase))
                {
                    if (settings.VoiceCommandDebug && !string.IsNullOrWhiteSpace(transcript))
                    {
                        Console.WriteLine($"Voice phrase routed to question handler: {transcript}");
                    }

                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        await onQuestionAsked(transcript).ConfigureAwait(false);
                    }

                    continue;
                }

                Console.WriteLine($"Voice command heard: {transcript}");
                await onCaptureRequested().ConfigureAwait(false);

                if (settings.VoiceCommandCooldown > TimeSpan.Zero)
                {
                    await Task.Delay(settings.VoiceCommandCooldown, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ReportStatus($"Voice command listener error: {FormatException(ex)}");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string FormatException(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            messages.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" -> ", messages);
    }

    private bool IsVoiceInputSuppressed()
    {
        return getVoiceInputSuppressedUntil() > DateTimeOffset.UtcNow;
    }

    private void ReportStatus(string status)
    {
        if (status == lastStatus)
        {
            return;
        }

        lastStatus = status;
        Console.ForegroundColor = status.Contains("error", StringComparison.OrdinalIgnoreCase)
            || status.Contains("No microphone", StringComparison.OrdinalIgnoreCase)
            ? ConsoleColor.Yellow
            : ConsoleColor.DarkGray;
        Console.WriteLine(status);
        Console.ResetColor();
    }
}

internal readonly record struct MicrophoneDevice(int DeviceNumber, string Name);

internal static class MicrophoneSelector
{
    public static MicrophoneDevice? SelectDevice(string activeSourceName, AppSettings settings)
    {
        var devices = ListDevices();
        if (devices.Count == 0)
        {
            return null;
        }

        var configuredDevice = activeSourceName.Contains("Viture", StringComparison.OrdinalIgnoreCase)
            ? settings.VitureMicDevice
            : settings.WebcamMicDevice;

        return FindConfiguredDevice(devices, configuredDevice)
            ?? FindHeuristicDevice(devices, activeSourceName)
            ?? devices[0];
    }

    private static IReadOnlyList<MicrophoneDevice> ListDevices()
    {
        var devices = new List<MicrophoneDevice>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var capabilities = WaveInEvent.GetCapabilities(i);
            devices.Add(new MicrophoneDevice(i, capabilities.ProductName));
        }

        return devices;
    }

    private static MicrophoneDevice? FindConfiguredDevice(IReadOnlyList<MicrophoneDevice> devices, string configuredDevice)
    {
        if (string.IsNullOrWhiteSpace(configuredDevice))
        {
            return null;
        }

        if (int.TryParse(configuredDevice, out var deviceNumber))
        {
            foreach (var device in devices)
            {
                if (device.DeviceNumber == deviceNumber)
                {
                    return device;
                }
            }

            return null;
        }

        foreach (var device in devices)
        {
            if (device.Name.Contains(configuredDevice, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }

    private static MicrophoneDevice? FindHeuristicDevice(IReadOnlyList<MicrophoneDevice> devices, string activeSourceName)
    {
        var terms = activeSourceName.Contains("Viture", StringComparison.OrdinalIgnoreCase)
            ? new[] { "viture", "beast", "glasses", "xr" }
            : new[] { "webcam", "camera", "camara", "cÃ¡mara", "cam" };

        foreach (var term in terms)
        {
            var match = devices.FirstOrDefault(device =>
                device.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Name))
            {
                return match;
            }
        }

        return null;
    }
}

internal sealed record AudioClip(byte[] WavBytes, double Rms);

internal static class MicrophoneRecorder
{
    public static async Task<AudioClip> RecordWavAsync(
        int deviceNumber,
        TimeSpan maxDuration,
        double speechThresholdRms,
        TimeSpan silenceAfterSpeech,
        Action? onSpeechDetected,
        CancellationToken cancellationToken)
    {
        var waveFormat = new WaveFormat(16000, 16, 1);
        using var waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = waveFormat,
            BufferMilliseconds = 100
        };
        using var stream = new MemoryStream();
        using var writer = new WaveFileWriter(stream, waveFormat);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateLock = new object();
        long squareSum = 0;
        long sampleCount = 0;
        var speechStarted = false;
        var speechCuePlayed = false;
        var lastSpeechAt = DateTimeOffset.MinValue;

        waveIn.DataAvailable += (_, args) =>
        {
            writer.Write(args.Buffer, 0, args.BytesRecorded);
            long chunkSquareSum = 0;
            long chunkSampleCount = 0;
            for (var i = 0; i + 1 < args.BytesRecorded; i += 2)
            {
                var sample = BitConverter.ToInt16(args.Buffer, i);
                var squared = (long)sample * sample;
                squareSum += squared;
                sampleCount++;
                chunkSquareSum += squared;
                chunkSampleCount++;
            }

            var chunkRms = chunkSampleCount == 0
                ? 0
                : Math.Sqrt(chunkSquareSum / (double)chunkSampleCount) / short.MaxValue;

            if (chunkRms >= speechThresholdRms)
            {
                var shouldPlayCue = false;
                lock (stateLock)
                {
                    speechStarted = true;
                    lastSpeechAt = DateTimeOffset.UtcNow;
                    if (!speechCuePlayed)
                    {
                        speechCuePlayed = true;
                        shouldPlayCue = true;
                    }
                }

                if (shouldPlayCue)
                {
                    _ = Task.Run(() => onSpeechDetected?.Invoke());
                }
            }
        };
        waveIn.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                stopped.TrySetException(args.Exception);
                return;
            }

            stopped.TrySetResult();
        };

        waveIn.StartRecording();
        try
        {
            var deadline = DateTimeOffset.UtcNow + maxDuration;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                lock (stateLock)
                {
                    if (speechStarted && DateTimeOffset.UtcNow - lastSpeechAt >= silenceAfterSpeech)
                    {
                        break;
                    }
                }
            }

            waveIn.StopRecording();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            waveIn.StopRecording();
            throw;
        }

        writer.Flush();
        var rms = sampleCount == 0
            ? 0
            : Math.Sqrt(squareSum / (double)sampleCount) / short.MaxValue;

        return new AudioClip(stream.ToArray(), rms);
    }
}

internal static class VoiceCommandMatcher
{
    public static bool IsMatch(string transcript, string commandPhrase)
    {
        var normalizedTranscript = Normalize(transcript);
        var normalizedCommand = Normalize(commandPhrase);

        return normalizedTranscript.Contains(normalizedCommand, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

}
