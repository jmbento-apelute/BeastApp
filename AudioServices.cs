internal readonly record struct AudioOutputDevice(string Id, string Name);

internal static class AudioOutputSelector
{
    public static AudioOutputDevice? SelectDevice(string activeSourceName, AppSettings settings)
    {
        var devices = ListDevices();
        if (devices.Count == 0)
        {
            return null;
        }

        var configuredDevice = activeSourceName.Contains("Viture", StringComparison.OrdinalIgnoreCase)
            ? settings.VitureSpeakerDevice
            : settings.WebcamSpeakerDevice;

        return FindConfiguredDevice(devices, configuredDevice)
            ?? FindHeuristicDevice(devices, activeSourceName);
    }

    private static IReadOnlyList<AudioOutputDevice> ListDevices()
    {
        var devices = new List<AudioOutputDevice>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            devices.Add(new AudioOutputDevice(device.ID, device.FriendlyName));
        }

        return devices;
    }

    private static AudioOutputDevice? FindConfiguredDevice(IReadOnlyList<AudioOutputDevice> devices, string configuredDevice)
    {
        if (string.IsNullOrWhiteSpace(configuredDevice))
        {
            return null;
        }

        foreach (var device in devices)
        {
            if (device.Name.Contains(configuredDevice, StringComparison.OrdinalIgnoreCase)
                || device.Id.Contains(configuredDevice, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }

    private static AudioOutputDevice? FindHeuristicDevice(IReadOnlyList<AudioOutputDevice> devices, string activeSourceName)
    {
        var terms = activeSourceName.Contains("Viture", StringComparison.OrdinalIgnoreCase)
            ? new[] { "viture", "beast", "glasses", "xr" }
            : Array.Empty<string>();

        foreach (var term in terms)
        {
            foreach (var device in devices)
            {
                if (device.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return device;
                }
            }
        }

        return null;
    }
}

internal sealed class AudioPlaybackService : IDisposable
{
    private const int CueSampleRate = 44100;

    public void PlayCaptureCompleteCue()
    {
        try
        {
            using var stream = new MemoryStream(CreateCameraCuePcm());
            using var reader = new RawSourceWaveStream(stream, new WaveFormat(CueSampleRate, 16, 1));
            using var output = new WaveOutEvent();
            output.Init(reader);
            output.Play();

            while (output.PlaybackState == PlaybackState.Playing)
            {
                Thread.Sleep(10);
            }
        }
        catch
        {
            // The capture cue should never block image analysis if audio playback is unavailable.
        }
    }

    public void PlayVoiceDetectedCue(AudioOutputDevice? outputDevice)
    {
        try
        {
            using var stream = new MemoryStream(CreateVoiceDetectedCuePcm());
            using var reader = new RawSourceWaveStream(stream, new WaveFormat(CueSampleRate, 16, 1));
            using IWavePlayer output = outputDevice is { } selectedDevice
                ? CreateWasapiOutput(selectedDevice)
                : new WaveOutEvent();
            output.Init(reader);
            output.Play();

            while (output.PlaybackState == PlaybackState.Playing)
            {
                Thread.Sleep(10);
            }
        }
        catch
        {
            // Voice detection feedback should never stop transcription.
        }
    }

    public void PlayMp3(byte[] mp3Bytes, AudioOutputDevice? outputDevice)
    {
        using var stream = new MemoryStream(mp3Bytes);
        using var reader = new Mp3FileReader(stream);
        using IWavePlayer output = outputDevice is { } selectedDevice
            ? CreateWasapiOutput(selectedDevice)
            : new WaveOutEvent();
        if (outputDevice is { } device)
        {
            Console.WriteLine($"Playing speech on: {device.Name}");
        }

        output.Init(reader);
        output.Play();

        while (output.PlaybackState == PlaybackState.Playing)
        {
            Thread.Sleep(50);
        }
    }

    public void Dispose()
    {
    }

    private static byte[] CreateCameraCuePcm()
    {
        const double durationSeconds = 0.16;
        var sampleCount = (int)(CueSampleRate * durationSeconds);
        var bytes = new byte[sampleCount * sizeof(short)];

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)CueSampleRate;
            var signal =
                Burst(t, 0.000, 0.045, 1800, 3600, 0.48) +
                Burst(t, 0.070, 0.040, 2500, 5000, 0.38);
            var sample = (short)(Math.Clamp(signal, -1.0, 1.0) * short.MaxValue);

            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[(i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return bytes;
    }

    private static byte[] CreateVoiceDetectedCuePcm()
    {
        const double durationSeconds = 0.09;
        var sampleCount = (int)(CueSampleRate * durationSeconds);
        var bytes = new byte[sampleCount * sizeof(short)];

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)CueSampleRate;
            var local = t / durationSeconds;
            var envelope = Math.Sin(Math.PI * local);
            var tone = Math.Sin(2 * Math.PI * 1100 * t);
            var sample = (short)(0.30 * envelope * tone * short.MaxValue);

            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[(i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return bytes;
    }

    private static IWavePlayer CreateWasapiOutput(AudioOutputDevice device)
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoint = enumerator.GetDevice(device.Id);
        return new WasapiOut(endpoint, AudioClientShareMode.Shared, false, 200);
    }

    private static double Burst(
        double t,
        double startSeconds,
        double lengthSeconds,
        double primaryFrequency,
        double secondaryFrequency,
        double volume)
    {
        var local = (t - startSeconds) / lengthSeconds;
        if (local is < 0 or > 1)
        {
            return 0;
        }

        var envelope = Math.Sin(Math.PI * local) * Math.Exp(-4.5 * local);
        var tone =
            Math.Sin(2 * Math.PI * primaryFrequency * t) +
            (0.35 * Math.Sin(2 * Math.PI * secondaryFrequency * t));

        return volume * envelope * tone;
    }
}
