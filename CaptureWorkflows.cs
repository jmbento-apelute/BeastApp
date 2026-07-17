internal sealed class CaptureFileStore
{
    private readonly AppSettings settings;

    public CaptureFileStore(AppSettings settings)
    {
        this.settings = settings;
    }

    public void SaveImageIfEnabled(CapturedImage image)
    {
        if (!settings.SaveCaptures)
        {
            return;
        }

        Directory.CreateDirectory(settings.CaptureSaveDirectory);
        var fileName = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Sanitize(image.Source.Name)}.jpg";
        var path = Path.Combine(settings.CaptureSaveDirectory, fileName);
        File.WriteAllBytes(path, image.JpegBytes);
        Console.WriteLine($"Capture saved: {path}");
    }

    public string CreateVideoPath(IImageCaptureSource source)
    {
        var fileName = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Sanitize(source.Name)}.avi";
        return Path.Combine(settings.VideoCaptureDirectory, fileName);
    }

    private static string Sanitize(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(invalidChars.Contains(character) ? '-' : character);
        }

        return builder.ToString().Replace(' ', '-');
    }
}

internal sealed class CaptureFallbackService
{
    private readonly IImageCaptureSource vitureSource;
    private readonly IImageCaptureSource webcamSource;
    private readonly CaptureFileStore fileStore;

    public CaptureFallbackService(
        IImageCaptureSource vitureSource,
        IImageCaptureSource webcamSource,
        CaptureFileStore fileStore)
    {
        this.vitureSource = vitureSource;
        this.webcamSource = webcamSource;
        this.fileStore = fileStore;
    }

    public async Task<CapturedImage> CaptureImageAsync(
        IImageCaptureSource requestedSource,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var jpeg = await requestedSource.CaptureJpegAsync(timeout, cancellationToken);
            return new CapturedImage(requestedSource, jpeg);
        }
        catch (Exception exception) when (ReferenceEquals(requestedSource, vitureSource))
        {
            ReportFallback("capture", exception);
            var jpeg = await webcamSource.CaptureJpegAsync(timeout, cancellationToken);
            return new CapturedImage(webcamSource, jpeg);
        }
    }

    public async Task<CapturedVideo> CaptureVideoAsync(
        IVideoCaptureSource requestedSource,
        TimeSpan duration,
        TimeSpan firstFrameTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CaptureVideoFromSourceAsync(
                requestedSource,
                duration,
                firstFrameTimeout,
                cancellationToken);
        }
        catch (Exception exception) when (ReferenceEquals(requestedSource, vitureSource))
        {
            ReportFallback("video capture", exception);
            return await CaptureVideoFromSourceAsync(
                (IVideoCaptureSource)webcamSource,
                duration,
                firstFrameTimeout,
                cancellationToken);
        }
    }

    private async Task<CapturedVideo> CaptureVideoFromSourceAsync(
        IVideoCaptureSource source,
        TimeSpan duration,
        TimeSpan firstFrameTimeout,
        CancellationToken cancellationToken)
    {
        var outputPath = fileStore.CreateVideoPath(source);
        await source.CaptureVideoAsync(duration, firstFrameTimeout, outputPath, cancellationToken);
        return new CapturedVideo(source, outputPath, duration);
    }

    private static void ReportFallback(string operation, Exception exception)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Viture {operation} failed. Falling back to webcam.");
        Console.WriteLine($"Viture error: {exception.GetType().Name}: {exception.Message}");
        Console.ResetColor();
    }
}
